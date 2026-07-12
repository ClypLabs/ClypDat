using Eve.Capture.Abstractions;
using FFmpeg.AutoGen;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using MapFlags = Vortice.Direct3D11.MapFlags;
using CpuAccessFlags = Vortice.Direct3D11.CpuAccessFlags;
using BindFlags = Vortice.Direct3D11.BindFlags;
using ResourceUsage = Vortice.Direct3D11.ResourceUsage;
using MapMode = Vortice.Direct3D11.MapMode;
using Texture2DDescription = Vortice.Direct3D11.Texture2DDescription;
using D3D11 = Vortice.Direct3D11.D3D11;
using DeviceCreationFlags = Vortice.Direct3D11.DeviceCreationFlags;

namespace Eve.App.Services;

// Phase 1 of the native capture engine (see plan): raw DXGI Desktop Duplication capture,
// direct h264_nvenc encode via libavcodec (FFmpeg.AutoGen P/Invoke), and a true in-memory
// packet ring buffer - replacing WindowsReplayBuffer's stop/start ScreenRecorderLib segment
// rotation (and the real-time gap that model has at every rotation boundary) with an encoder
// that never stops during normal operation.
//
// Scope of this first cut: primary monitor only, cropped to the detected game window's rect
// when the window lives on the primary monitor (falls back to full monitor otherwise), NVENC
// only (no software fallback yet - machines without NVENC should stay on Legacy/OBS for now).
// Audio is not yet wired in (Phase 2) - saved clips are video-only until that lands.
[SupportedOSPlatform("windows")]
public sealed class NativeReplayBuffer : IReplayBuffer
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly object _bufferLock = new();
    private readonly List<RingPacket> _packets = new();

    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private volatile bool _sessionActive;
    private AVRational _timeBase = new() { num = 1, den = 1_000_000 };
    private byte[]? _extraData;
    private int _outputWidth;
    private int _outputHeight;

    public NativeReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
    }

    public bool IsRecording => _sessionActive;
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);
    public event EventHandler? RecordingStopped;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionActive) return Task.CompletedTask;

        var config = _configProvider();
        Duration = TimeSpan.FromSeconds(Math.Clamp(config.DurationSeconds, 30, 1200));

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _captureCts = new CancellationTokenSource();
        var token = _captureCts.Token;
        _captureTask = Task.Factory.StartNew(
            () => CaptureLoop(token, ready),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _sessionActive = true;
        return ready.Task;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_sessionActive) return;
        _sessionActive = false;
        _captureCts?.Cancel();
        if (_captureTask is not null)
        {
            try { await _captureTask; }
            catch (OperationCanceledException) { }
        }

        lock (_bufferLock) _packets.Clear();
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null)
    {
        if (!_sessionActive) throw new InvalidOperationException("Replay buffer is not recording.");

        RingPacket[] window;
        lock (_bufferLock)
        {
            if (_packets.Count == 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

            var cutoffUtc = DateTime.UtcNow - Duration;
            var startIndex = _packets.FindLastIndex(p => p.WallClockUtc <= cutoffUtc && p.IsKeyframe);
            if (startIndex < 0) startIndex = _packets.FindIndex(p => p.IsKeyframe);
            if (startIndex < 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

            window = _packets.Skip(startIndex).ToArray();
        }

        if (window.Length == 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

        var config = _configProvider();
        var clipName = string.IsNullOrWhiteSpace(titleOverride) ? config.GameDisplayName : titleOverride;
        var outputPath = Path.Combine(outputFolder, ClipFileNaming.BuildFileName(clipName, DateTime.Now, "mp4"));

        await Task.Run(() => RemuxWindowToMp4(window, outputPath), cancellationToken);

        AppLog.Info($"Native replay saved (video-only, audio not yet wired): path={outputPath}, packets={window.Length}.");
        return outputPath;
    }

    public void SetCapturePaused(bool paused)
    {
        // No-op for now: the capture loop always runs while a session is active.
        // A pause would need to stop encoding without tearing down the ring buffer,
        // not currently needed since nothing calls this on the Windows Capture path.
    }

    public void Dispose()
    {
        _captureCts?.Cancel();
        _captureCts?.Dispose();
    }

    private unsafe void CaptureLoop(CancellationToken token, TaskCompletionSource ready)
    {
        ID3D11Device? device = null;
        ID3D11Texture2D? staging = null;
        IDXGIOutputDuplication? duplication = null;
        AVCodecContext* codecContext = null;
        SwsContext* swsContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;

        try
        {
            var config = _configProvider();
            device = CreateD3D11Device();
            duplication = CreateOutputDuplication(device, out var monitorWidth, out var monitorHeight);

            var captureRect = ResolveCaptureRect(config, monitorWidth, monitorHeight);
            var (outputWidth, outputHeight) = CaptureOutputSize(config, captureRect.Width, captureRect.Height);
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;

            staging = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)monitorWidth,
                Height = (uint)monitorHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None
            });

            codecContext = CreateEncoder(config, outputWidth, outputHeight, out var codecTimeBase);
            _timeBase = codecTimeBase;

            swsContext = ffmpeg.sws_getContext(
                captureRect.Width, captureRect.Height, AVPixelFormat.AV_PIX_FMT_BGRA,
                outputWidth, outputHeight, AVPixelFormat.AV_PIX_FMT_NV12,
                2 /* SWS_BILINEAR */, null, null, null);
            if (swsContext is null) throw new InvalidOperationException("sws_getContext failed.");

            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            frame->width = outputWidth;
            frame->height = outputHeight;
            ffmpeg.av_frame_get_buffer(frame, 32);

            packet = ffmpeg.av_packet_alloc();

            AppLog.Info($"Native capture started: monitor={monitorWidth}x{monitorHeight}, crop={captureRect.Width}x{captureRect.Height}, output={outputWidth}x{outputHeight}.");
            ready.TrySetResult();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var lastForcedKeyframe = TimeSpan.Zero;
            var lastRectRefresh = TimeSpan.Zero;
            var lastEncodedAt = TimeSpan.Zero;
            var targetFrameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(config.FrameRate, 15, 240));

            while (!token.IsCancellationRequested)
            {
                // The crop rect is resolved once above from whatever window was
                // foreground when capture started - if that wasn't the game yet (e.g.
                // the buffer armed before the user tabbed in), it would otherwise stay
                // wrong (full desktop) for the entire session, since this backend never
                // rotates/restarts the way WindowsReplayBuffer does to naturally pick up
                // fresh config. Re-resolve periodically instead.
                if (stopwatch.Elapsed - lastRectRefresh >= TimeSpan.FromSeconds(1))
                {
                    lastRectRefresh = stopwatch.Elapsed;
                    var freshRect = ResolveCaptureRect(_configProvider(), monitorWidth, monitorHeight);
                    if (freshRect.Width != captureRect.Width || freshRect.Height != captureRect.Height)
                    {
                        // Crop size changed (different window/aspect) - rebuild the scaler
                        // against the new source size, keeping the same output resolution
                        // so the encoder doesn't need to be reopened (a same-size crop
                        // move, the common case, only updates X/Y below, no rebuild).
                        var newSws = ffmpeg.sws_getContext(
                            freshRect.Width, freshRect.Height, AVPixelFormat.AV_PIX_FMT_BGRA,
                            outputWidth, outputHeight, AVPixelFormat.AV_PIX_FMT_NV12,
                            2 /* SWS_BILINEAR */, null, null, null);
                        if (newSws is not null)
                        {
                            ffmpeg.sws_freeContext(swsContext);
                            swsContext = newSws;
                        }
                    }

                    captureRect = freshRect;
                }

                var acquireResult = duplication.AcquireNextFrame(500, out var frameInfo, out var resource);
                if (acquireResult.Failure)
                {
                    if (acquireResult.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) continue;

                    if (acquireResult.Code == Vortice.DXGI.ResultCode.AccessLost.Code)
                    {
                        AppLog.Info("Native capture: DXGI access lost, recreating duplication.");
                        duplication.Dispose();
                        duplication = CreateOutputDuplication(device, out _, out _);
                        continue;
                    }

                    AppLog.Error($"Native capture: AcquireNextFrame failed ({acquireResult}).", null);
                    break;
                }

                // DXGI signals a new frame on ANY screen change (cursor movement,
                // animations, etc.), which can be far more often than the target frame
                // rate - especially on high refresh-rate monitors. Encoding every single
                // one oversaturates NVENC and, worse, the synchronous GPU->CPU staging
                // readback below, which was making capture fall behind real time
                // ("sluggish"/laggy output) instead of just dropping the excess frames.
                if (stopwatch.Elapsed - lastEncodedAt < targetFrameInterval)
                {
                    resource.Dispose();
                    duplication.ReleaseFrame();
                    continue;
                }
                lastEncodedAt = stopwatch.Elapsed;

                using (resource)
                {
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    device.ImmediateContext.CopyResource(staging, texture);
                }
                duplication.ReleaseFrame();

                var mapped = device.ImmediateContext.Map(staging, 0, MapMode.Read, MapFlags.None);
                try
                {
                    var srcPointer = (byte*)mapped.DataPointer + captureRect.Y * (int)mapped.RowPitch + captureRect.X * 4;
                    var srcData = new byte*[1] { srcPointer };
                    var srcStride = new int[1] { (int)mapped.RowPitch };
                    ffmpeg.sws_scale(swsContext, srcData, srcStride, 0, captureRect.Height, frame->data, frame->linesize);
                }
                finally
                {
                    device.ImmediateContext.Unmap(staging, 0);
                }

                frame->pts = stopwatch.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;

                // Force a keyframe periodically so the ring buffer always has a nearby
                // point to start a save-window at without waiting on the encoder's own
                // GOP schedule.
                if (stopwatch.Elapsed - lastForcedKeyframe >= TimeSpan.FromSeconds(2))
                {
                    frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                    lastForcedKeyframe = stopwatch.Elapsed;
                }
                else
                {
                    frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                }

                if (ffmpeg.avcodec_send_frame(codecContext, frame) == 0)
                {
                    DrainToRingBuffer(codecContext, packet);
                }

                TrimRingBuffer();
            }

            // flush
            ffmpeg.avcodec_send_frame(codecContext, null);
            DrainToRingBuffer(codecContext, packet);
        }
        catch (Exception error)
        {
            AppLog.Error("Native capture loop failed.", error);
            ready.TrySetException(error);
            _sessionActive = false;
            RecordingStopped?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (frame is not null) { var f = frame; ffmpeg.av_frame_free(&f); }
            if (packet is not null) { var p = packet; ffmpeg.av_packet_free(&p); }
            if (swsContext is not null) ffmpeg.sws_freeContext(swsContext);
            if (codecContext is not null) { var c = codecContext; ffmpeg.avcodec_free_context(&c); }
            duplication?.Dispose();
            staging?.Dispose();
            device?.Dispose();
        }
    }

    private unsafe void DrainToRingBuffer(AVCodecContext* codecContext, AVPacket* packet)
    {
        while (true)
        {
            var receiveResult = ffmpeg.avcodec_receive_packet(codecContext, packet);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF) break;
            if (receiveResult < 0) break;

            var isKeyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            var data = new byte[packet->size];
            Marshal.Copy((IntPtr)packet->data, data, 0, packet->size);

            if (_extraData is null && codecContext->extradata_size > 0)
            {
                _extraData = new byte[codecContext->extradata_size];
                Marshal.Copy((IntPtr)codecContext->extradata, _extraData, 0, codecContext->extradata_size);
            }

            lock (_bufferLock)
            {
                _packets.Add(new RingPacket(data, packet->pts, isKeyframe, DateTime.UtcNow));
            }

            ffmpeg.av_packet_unref(packet);
        }
    }

    private void TrimRingBuffer()
    {
        var cutoff = DateTime.UtcNow - Duration - TimeSpan.FromSeconds(5);
        lock (_bufferLock)
        {
            var removeCount = 0;
            while (removeCount < _packets.Count && _packets[removeCount].WallClockUtc < cutoff) removeCount++;
            if (removeCount > 0) _packets.RemoveRange(0, removeCount);
        }
    }

    private unsafe void RemuxWindowToMp4(RingPacket[] window, string outputPath)
    {
        AVFormatContext* formatContext = null;
        try
        {
            ffmpeg.avformat_alloc_output_context2(&formatContext, null, "mp4", outputPath);
            if (formatContext is null) throw new InvalidOperationException("avformat_alloc_output_context2 failed.");

            var stream = ffmpeg.avformat_new_stream(formatContext, null);
            stream->time_base = _timeBase;
            stream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            stream->codecpar->codec_id = AVCodecID.AV_CODEC_ID_H264;
            stream->codecpar->width = _outputWidth;
            stream->codecpar->height = _outputHeight;

            if (_extraData is { Length: > 0 })
            {
                var extraDataPtr = (byte*)ffmpeg.av_mallocz((ulong)_extraData.Length);
                Marshal.Copy(_extraData, 0, (IntPtr)extraDataPtr, _extraData.Length);
                stream->codecpar->extradata = extraDataPtr;
                stream->codecpar->extradata_size = _extraData.Length;
            }

            if ((formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                AVIOContext* ioContext;
                var openResult = ffmpeg.avio_open(&ioContext, outputPath, ffmpeg.AVIO_FLAG_WRITE);
                if (openResult < 0) throw new InvalidOperationException($"avio_open failed ({openResult}).");
                formatContext->pb = ioContext;
            }

            var headerResult = ffmpeg.avformat_write_header(formatContext, null);
            if (headerResult < 0) throw new InvalidOperationException($"avformat_write_header failed ({headerResult}).");

            var basePts = window[0].PtsMs;
            var packet = ffmpeg.av_packet_alloc();
            try
            {
                foreach (var ringPacket in window)
                {
                    fixed (byte* dataPointer = ringPacket.Data)
                    {
                        ffmpeg.av_new_packet(packet, ringPacket.Data.Length);
                        Marshal.Copy(ringPacket.Data, 0, (IntPtr)packet->data, ringPacket.Data.Length);
                        packet->pts = packet->dts = ringPacket.PtsMs - basePts;
                        packet->stream_index = stream->index;
                        if (ringPacket.IsKeyframe) packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;
                        ffmpeg.av_interleaved_write_frame(formatContext, packet);
                        ffmpeg.av_packet_unref(packet);
                    }
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
            }

            ffmpeg.av_write_trailer(formatContext);
        }
        finally
        {
            if (formatContext is not null)
            {
                if ((formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && formatContext->pb is not null)
                {
                    ffmpeg.avio_closep(&formatContext->pb);
                }
                ffmpeg.avformat_free_context(formatContext);
            }
        }
    }

    private static unsafe AVCodecContext* CreateEncoder(ReplayBufferConfig config, int width, int height, out AVRational timeBase)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
        if (codec is null) throw new InvalidOperationException("h264_nvenc encoder not available on this machine.");

        timeBase = new AVRational { num = 1, den = 1_000_000 };
        var codecContext = ffmpeg.avcodec_alloc_context3(codec);
        codecContext->width = width;
        codecContext->height = height;
        codecContext->time_base = timeBase;
        codecContext->framerate = new AVRational { num = Math.Clamp(config.FrameRate, 15, 240), den = 1 };
        codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        codecContext->bit_rate = CaptureBitrate(config);
        codecContext->gop_size = 240;
        codecContext->max_b_frames = 0;
        codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        var openResult = ffmpeg.avcodec_open2(codecContext, codec, null);
        if (openResult < 0) throw new InvalidOperationException($"avcodec_open2 failed ({openResult}).");

        return codecContext;
    }

    private static int CaptureBitrate(ReplayBufferConfig config)
    {
        var height = Math.Clamp(config.MaxHeight, 480, 2160);
        var frameRate = Math.Clamp(config.FrameRate, 15, 240);
        var megapixels = height switch
        {
            >= 2160 => 8.3,
            >= 1440 => 3.7,
            >= 1080 => 2.1,
            >= 720 => 0.9,
            _ => 0.4
        };
        return (int)Math.Clamp(megapixels * frameRate * 130_000, 8_000_000, 80_000_000);
    }

    private static (int Width, int Height) CaptureOutputSize(ReplayBufferConfig config, int sourceWidth, int sourceHeight)
    {
        var height = Math.Clamp(config.MaxHeight, 480, 2160);
        var aspect = sourceWidth / (double)Math.Max(1, sourceHeight);
        var width = MakeEven((int)Math.Round(height * aspect));
        return (width, MakeEven(height));
    }

    private static int MakeEven(int value)
    {
        value = Math.Max(2, value);
        return value % 2 == 0 ? value : value + 1;
    }

    private static CaptureRect ResolveCaptureRect(ReplayBufferConfig config, int monitorWidth, int monitorHeight)
    {
        if (config.GameWindowHandle != 0 && IsWindow(config.GameWindowHandle) && GetWindowRect(config.GameWindowHandle, out var rect))
        {
            var x = Math.Clamp(rect.Left, 0, monitorWidth - 1);
            var y = Math.Clamp(rect.Top, 0, monitorHeight - 1);
            var width = Math.Clamp(rect.Right - rect.Left, 2, monitorWidth - x);
            var height = Math.Clamp(rect.Bottom - rect.Top, 2, monitorHeight - y);
            if (width > 16 && height > 16) return new CaptureRect(x, y, MakeEven(width), MakeEven(height));
        }

        return new CaptureRect(0, 0, monitorWidth, monitorHeight);
    }

    private static ID3D11Device CreateD3D11Device()
    {
        var levels = new[]
        {
            Vortice.Direct3D.FeatureLevel.Level_11_1,
            Vortice.Direct3D.FeatureLevel.Level_11_0,
            Vortice.Direct3D.FeatureLevel.Level_10_1,
        };
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device, out _, out _).CheckError();
        return device!;
    }

    private static IDXGIOutputDuplication CreateOutputDuplication(ID3D11Device device, out int width, out int height)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
        adapter.EnumOutputs(0, out var output).CheckError();
        using var _output = output;
        using var output1 = output.QueryInterface<IDXGIOutput1>();
        var desc = output.Description;
        width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
        height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
        return output1.DuplicateOutput(device);
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RectStruct rect);

    private readonly record struct CaptureRect(int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly record struct RingPacket(byte[] Data, long PtsMs, bool IsKeyframe, DateTime WallClockUtc);
}
