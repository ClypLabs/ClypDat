using Eve.Capture.Abstractions;
using FFmpeg.AutoGen;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
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

// Phase 1 of the native capture engine (see plan): true per-window capture via
// Windows.Graphics.Capture, direct h264_nvenc encode via libavcodec (FFmpeg.AutoGen
// P/Invoke), and a true in-memory packet ring buffer - replacing WindowsReplayBuffer's
// stop/start ScreenRecorderLib segment rotation (and the real-time gap that model has at
// every rotation boundary) with an encoder that never stops during normal operation.
//
// WGC (not raw DXGI Desktop Duplication) captures the game window's actual content
// directly, so it keeps recording the game specifically through alt-tabs/overlays/
// occlusion - a fixed screen-region crop (the original approach) would instead show
// whatever's on screen at that rectangle, including other apps. Falls back to capturing
// the primary monitor when no game window is detected. NVENC only for now (no software
// fallback - machines without NVENC should stay on Legacy/OBS). Audio (Phase 2) reuses
// AudioCapturePipeline - the same Game/Chat/Microphone routing, WASAPI capture, and mux
// logic WindowsReplayBuffer uses, via its own independent instance.
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class NativeReplayBuffer : IReplayBuffer
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly AudioCapturePipeline _audio;
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
        var bufferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "native-replay-buffer");
        _audio = new AudioCapturePipeline(bufferFolder);
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

        _audio.Start(config);
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

        _audio.Stop();
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

        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"eve-native-video-{Guid.NewGuid():N}.mp4");
        var snapshots = new List<string>();
        try
        {
            await Task.Run(() => RemuxWindowToMp4(window, tempVideoPath), cancellationToken);

            // The ring buffer already remuxes exactly the desired window starting at a
            // real keyframe - no offset/trim needed here the way WindowsReplayBuffer's
            // keyframe-seek fallback requires, so this is a single segment window
            // spanning the whole saved clip.
            var windowStartUtc = window[0].WallClockUtc;
            var windowDurationSeconds = Math.Max(1, (window[^1].WallClockUtc - windowStartUtc).TotalSeconds);
            var segmentWindows = new List<(DateTime StartUtc, double DurationSeconds)> { (windowStartUtc, windowDurationSeconds) };

            var (gamePath, chatPath, microphonePath) = await _audio.BuildAlignedTracksAsync(segmentWindows, config, snapshots, cancellationToken);

            var muxArgs = new[]
            {
                "-y",
                "-i", tempVideoPath,
                "-i", gamePath,
                "-i", chatPath,
                "-i", microphonePath,
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a",
                "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                "-metadata:s:a:0", "title=Game Audio",
                "-metadata:s:a:1", "title=Chat Audio",
                "-metadata:s:a:2", "title=Microphone",
                "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("EVE Native")}",
                outputPath
            };
            var result = await AudioCapturePipeline.RunProcessAsync("ffmpeg", muxArgs, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg mux failed." : result.Error);
            }
        }
        finally
        {
            AudioCapturePipeline.TryDelete(tempVideoPath);
            foreach (var snapshot in snapshots) AudioCapturePipeline.TryDelete(snapshot);
        }

        AppLog.Info($"Native replay saved: path={outputPath}, packets={window.Length}.");
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
        IDirect3DDevice? winrtDevice = null;
        GraphicsCaptureItem? item = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        AVCodecContext* codecContext = null;
        SwsContext* swsContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        AVFormatContext* fullSessionFormatContext = null;
        AVStream* fullSessionStream = null;
        var fullSessionTempVideoPath = string.Empty;
        var fullSessionFinalOutputPath = string.Empty;
        var fullSessionStartUtc = DateTime.UtcNow;

        try
        {
            var config = _configProvider();
            device = CreateD3D11Device();
            winrtDevice = CaptureInterop.CreateDirect3DDevice(device);

            var targetHandle = ResolveTargetWindow(config);
            item = CreateItemFor(targetHandle);
            var captureWidth = item.Size.Width;
            var captureHeight = item.Size.Height;

            var (outputWidth, outputHeight) = CaptureOutputSize(config, captureWidth, captureHeight);
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;

            staging = CreateStagingTexture(device, captureWidth, captureHeight);
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = framePool.CreateCaptureSession(item);
            TryDisableCaptureBorder(session);
            session.StartCapture();

            codecContext = CreateEncoder(config, outputWidth, outputHeight, out var codecTimeBase);
            _timeBase = codecTimeBase;

            if (InitFullSessionWriter(config, codecContext, out fullSessionFormatContext, out fullSessionStream, out fullSessionTempVideoPath, out fullSessionFinalOutputPath))
            {
                fullSessionStartUtc = DateTime.UtcNow;
            }

            swsContext = CreateScaler(captureWidth, captureHeight, outputWidth, outputHeight);

            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            frame->width = outputWidth;
            frame->height = outputHeight;
            ffmpeg.av_frame_get_buffer(frame, 32);

            packet = ffmpeg.av_packet_alloc();

            AppLog.Info($"Native capture started (Windows.Graphics.Capture): target={(targetHandle != 0 ? "window" : "primary monitor")}, source={captureWidth}x{captureHeight}, output={outputWidth}x{outputHeight}.");
            ready.TrySetResult();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var lastForcedKeyframe = TimeSpan.Zero;
            var lastTargetRefresh = TimeSpan.Zero;
            var lastEncodedAt = TimeSpan.Zero;
            var targetFrameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(config.FrameRate, 15, 240));
            var lastDiagLog = TimeSpan.Zero;
            var framesSeen = 0;
            var framesEncoded = 0;

            while (!token.IsCancellationRequested)
            {
                if (stopwatch.Elapsed - lastDiagLog >= TimeSpan.FromSeconds(2))
                {
                    lastDiagLog = stopwatch.Elapsed;
                    AppLog.Info($"Native capture diag: framesSeen={framesSeen}, framesEncoded={framesEncoded}, ringPackets={_packets.Count}.");
                }

                // Re-check which window/monitor we should be capturing every second -
                // the detected game can change (switch games, close the game) mid-session,
                // and this backend never rotates/restarts the way WindowsReplayBuffer does
                // to naturally pick up fresh config.
                if (stopwatch.Elapsed - lastTargetRefresh >= TimeSpan.FromSeconds(1))
                {
                    lastTargetRefresh = stopwatch.Elapsed;
                    var freshHandle = ResolveTargetWindow(_configProvider());
                    if (freshHandle != targetHandle)
                    {
                        targetHandle = freshHandle;
                        session.Dispose();
                        framePool.Dispose();
                        item = CreateItemFor(targetHandle);
                        captureWidth = item.Size.Width;
                        captureHeight = item.Size.Height;
                        staging.Dispose();
                        staging = CreateStagingTexture(device, captureWidth, captureHeight);
                        framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
                        session = framePool.CreateCaptureSession(item);
                        TryDisableCaptureBorder(session);
                        session.StartCapture();
                        ffmpeg.sws_freeContext(swsContext);
                        swsContext = CreateScaler(captureWidth, captureHeight, outputWidth, outputHeight);
                    }
                }

                using var wgcFrame = framePool.TryGetNextFrame();
                if (wgcFrame is null)
                {
                    Thread.Sleep(4);
                    continue;
                }
                framesSeen++;

                if (wgcFrame.ContentSize.Width != captureWidth || wgcFrame.ContentSize.Height != captureHeight)
                {
                    // Window resized - rebuild the scaler/frame pool/staging texture against
                    // the new source size, keeping the same output resolution so the encoder
                    // doesn't need to be reopened (may letterbox/stretch slightly until the
                    // next resize, acceptable for this experimental backend).
                    captureWidth = wgcFrame.ContentSize.Width;
                    captureHeight = wgcFrame.ContentSize.Height;
                    framePool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, wgcFrame.ContentSize);
                    staging.Dispose();
                    staging = CreateStagingTexture(device, captureWidth, captureHeight);
                    ffmpeg.sws_freeContext(swsContext);
                    swsContext = CreateScaler(captureWidth, captureHeight, outputWidth, outputHeight);
                    continue;
                }

                if (stopwatch.Elapsed - lastEncodedAt < targetFrameInterval) continue;
                lastEncodedAt = stopwatch.Elapsed;
                framesEncoded++;

                using (var texture = CaptureInterop.GetTexture(wgcFrame.Surface))
                {
                    device.ImmediateContext.CopyResource(staging, texture);
                }

                var mapped = device.ImmediateContext.Map(staging, 0, MapMode.Read, MapFlags.None);
                try
                {
                    var srcData = new byte*[1] { (byte*)mapped.DataPointer };
                    var srcStride = new int[1] { (int)mapped.RowPitch };
                    ffmpeg.sws_scale(swsContext, srcData, srcStride, 0, captureHeight, frame->data, frame->linesize);
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
                    DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream);
                }

                TrimRingBuffer();
            }

            // flush
            ffmpeg.avcodec_send_frame(codecContext, null);
            DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream);
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
            FinalizeFullSessionWriter(fullSessionFormatContext);
            if (!string.IsNullOrEmpty(fullSessionTempVideoPath))
            {
                FinalizeFullSessionRecording(_configProvider(), fullSessionStartUtc, fullSessionTempVideoPath, fullSessionFinalOutputPath);
            }
            if (codecContext is not null) { var c = codecContext; ffmpeg.avcodec_free_context(&c); }
            session?.Dispose();
            framePool?.Dispose();
            staging?.Dispose();
            device?.Dispose();
        }
    }

    private static unsafe SwsContext* CreateScaler(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var swsContext = ffmpeg.sws_getContext(
            sourceWidth, sourceHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
            outputWidth, outputHeight, AVPixelFormat.AV_PIX_FMT_NV12,
            2 /* SWS_BILINEAR */, null, null, null);
        if (swsContext is null) throw new InvalidOperationException("sws_getContext failed.");
        return swsContext;
    }

    private static ID3D11Texture2D CreateStagingTexture(ID3D11Device device, int width, int height)
    {
        return device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        });
    }

    private static void TryDisableCaptureBorder(GraphicsCaptureSession session)
    {
        // Windows 11 draws a colored "this is being captured" border around the
        // target window/monitor by default (same indicator screen-share tools show) -
        // IsBorderRequired only exists on newer Windows builds (GraphicsCaptureSession3),
        // so this is a best-effort no-op on older ones rather than a hard requirement.
        try
        {
            session.IsBorderRequired = false;
        }
        catch
        {
            // Property unavailable on this Windows build - border stays, not fatal.
        }
    }

    private static GraphicsCaptureItem CreateItemFor(nint windowHandle)
    {
        return windowHandle != 0
            ? CaptureInterop.CreateItemForWindow(windowHandle)
            : CaptureInterop.CreateItemForMonitor(GetPrimaryMonitorHandle());
    }

    private static nint ResolveTargetWindow(ReplayBufferConfig config)
    {
        return config.GameWindowHandle != 0 && IsWindow(config.GameWindowHandle) ? config.GameWindowHandle : 0;
    }

    private static nint GetPrimaryMonitorHandle()
    {
        const uint MONITOR_DEFAULTTOPRIMARY = 1;
        return MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
    }

    private unsafe void DrainToRingBuffer(AVCodecContext* codecContext, AVPacket* packet, AVFormatContext* fullSessionFormatContext, AVStream* fullSessionStream)
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

            if (fullSessionFormatContext is not null)
            {
                var clonedPacket = ffmpeg.av_packet_clone(packet);
                if (clonedPacket is not null)
                {
                    clonedPacket->stream_index = fullSessionStream->index;
                    ffmpeg.av_interleaved_write_frame(fullSessionFormatContext, clonedPacket);
                    var cp = clonedPacket;
                    ffmpeg.av_packet_free(&cp);
                }
            }

            ffmpeg.av_packet_unref(packet);
        }
    }

    // Writes to a temp path during the session (not the user's chosen folder directly) -
    // final output only gets the audio-muxed file once the session ends, via
    // FinalizeFullSessionRecording. The video itself is written incrementally as
    // packets arrive (no separate encode pass), same as the ring buffer.
    private static unsafe bool InitFullSessionWriter(ReplayBufferConfig config, AVCodecContext* codecContext, out AVFormatContext* resultFormatContext, out AVStream* resultStream, out string tempVideoPath, out string finalOutputPath)
    {
        resultFormatContext = null;
        resultStream = null;
        tempVideoPath = string.Empty;
        finalOutputPath = string.Empty;
        if (!config.FullSessionRecordingEnabled || string.IsNullOrWhiteSpace(config.FullSessionRecordingFolder)) return false;

        try
        {
            Directory.CreateDirectory(config.FullSessionRecordingFolder);
            var sessionLabel = string.IsNullOrWhiteSpace(config.GameDisplayName) ? "Session" : $"Session - {config.GameDisplayName}";
            finalOutputPath = Path.Combine(config.FullSessionRecordingFolder, ClipFileNaming.BuildFileName(sessionLabel, DateTime.Now, "mp4"));
            tempVideoPath = Path.Combine(Path.GetTempPath(), $"eve-full-session-video-{Guid.NewGuid():N}.mp4");

            AVFormatContext* formatContext = null;
            ffmpeg.avformat_alloc_output_context2(&formatContext, null, "mp4", tempVideoPath);
            if (formatContext is null) return false;

            var stream = ffmpeg.avformat_new_stream(formatContext, null);
            if (ffmpeg.avcodec_parameters_from_context(stream->codecpar, codecContext) < 0)
            {
                ffmpeg.avformat_free_context(formatContext);
                return false;
            }
            stream->time_base = codecContext->time_base;

            if ((formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                AVIOContext* ioContext;
                if (ffmpeg.avio_open(&ioContext, tempVideoPath, ffmpeg.AVIO_FLAG_WRITE) < 0)
                {
                    ffmpeg.avformat_free_context(formatContext);
                    return false;
                }
                formatContext->pb = ioContext;
            }

            if (ffmpeg.avformat_write_header(formatContext, null) < 0)
            {
                ffmpeg.avformat_free_context(formatContext);
                return false;
            }

            AppLog.Info($"Native full session recording started: temp={tempVideoPath}, final={finalOutputPath}.");
            resultFormatContext = formatContext;
            resultStream = stream;
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("Full session recording init failed", error);
            return false;
        }
    }

    private static unsafe void FinalizeFullSessionWriter(AVFormatContext* formatContext)
    {
        if (formatContext is null) return;
        try
        {
            ffmpeg.av_write_trailer(formatContext);
        }
        catch (Exception error)
        {
            AppLog.Error("Full session recording finalize failed", error);
        }
        finally
        {
            if ((formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && formatContext->pb is not null)
            {
                ffmpeg.avio_closep(&formatContext->pb);
            }

            ffmpeg.avformat_free_context(formatContext);
        }
    }

    // Runs once, after the temp video is fully written and closed - builds Game/Chat/
    // Microphone tracks for the whole session's wall-clock window (same AudioCapturePipeline
    // used for clip saves, already running the whole time regardless) and muxes them
    // against the temp video into the user's chosen folder. -c:v copy keeps this fast
    // even for a multi-hour session.
    private void FinalizeFullSessionRecording(ReplayBufferConfig config, DateTime sessionStartUtc, string tempVideoPath, string finalOutputPath)
    {
        if (string.IsNullOrEmpty(tempVideoPath) || string.IsNullOrEmpty(finalOutputPath)) return;

        var snapshots = new List<string>();
        try
        {
            var durationSeconds = Math.Max(1, (DateTime.UtcNow - sessionStartUtc).TotalSeconds);
            var segmentWindows = new List<(DateTime StartUtc, double DurationSeconds)> { (sessionStartUtc, durationSeconds) };

            var (gamePath, chatPath, microphonePath) = _audio
                .BuildAlignedTracksAsync(segmentWindows, config, snapshots, CancellationToken.None)
                .GetAwaiter().GetResult();

            var muxArgs = new[]
            {
                "-y",
                "-i", tempVideoPath,
                "-i", gamePath,
                "-i", chatPath,
                "-i", microphonePath,
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a",
                "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                "-metadata:s:a:0", "title=Game Audio",
                "-metadata:s:a:1", "title=Chat Audio",
                "-metadata:s:a:2", "title=Microphone",
                "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("EVE Native Full Session")}",
                finalOutputPath
            };
            var result = AudioCapturePipeline.RunProcessAsync("ffmpeg", muxArgs, CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode != 0)
            {
                AppLog.Error($"Full session recording final mux failed: {result.Error}");
            }
            else
            {
                AppLog.Info($"Native full session recording saved: path={finalOutputPath}.");
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Full session recording finalize/mux failed", error);
        }
        finally
        {
            AudioCapturePipeline.TryDelete(tempVideoPath);
            foreach (var snapshot in snapshots) AudioCapturePipeline.TryDelete(snapshot);
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

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointStruct pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    private readonly record struct RingPacket(byte[] Data, long PtsMs, bool IsKeyframe, DateTime WallClockUtc);
}
