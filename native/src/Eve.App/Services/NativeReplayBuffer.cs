using Eve.Capture.Abstractions;
using FFmpeg.AutoGen;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using SharpGen.Runtime;
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

// Native capture engine: direct h264_nvenc encode via libavcodec
// (FFmpeg.AutoGen P/Invoke) and a true in-memory packet ring buffer - replacing
// WindowsReplayBuffer's stop/start ScreenRecorderLib segment rotation (and the
// real-time gap that model has at every rotation boundary) with an encoder that
// never stops during normal operation.
//
// Capture source is DXGI Desktop Duplication (IDXGIOutputDuplication), cropped to
// the target window's rect every frame, instead of Windows.Graphics.Capture -
// WGC's FrameArrived delivery measured a hard ~40-46fps ceiling on this hardware
// regardless of target fps/resolution (confirmed via GPUView trace: the GPU
// queues themselves and DWM's own composition rate both run far faster, so the
// bottleneck was specifically WGC's internal frame pacing, invisible to and
// unfixable from application code - ten targeted fixes to buffer depth,
// threading, timer resolution, and consumption model all measured zero effect).
// Desktop Duplication is the same lower-level API OBS and this app's own Legacy/
// ScreenRecorderLib backend already use, both proven to hit full target fps on
// this same machine.
//
// The tradeoff: Desktop Duplication captures the composited desktop, not a
// specific window's content directly, so it can't stay "attached" to a window
// through occlusion the way WGC does - if another window covers the crop
// region, duplication would show that window's content instead. Solved by
// checking whether the target window is actually the foreground window every
// frame; when it isn't (alt-tabbed away, minimized, covered), the last
// successfully captured frame is re-submitted to the encoder instead of a fresh
// (potentially other-app) capture, so the recording visually freezes rather
// than leaking other windows' content. Each freeze/resume transition is logged
// as a wall-clock event so SaveReplayAsync can tell the editor which parts of a
// saved clip were frozen, via a "Recording Paused" sidecar.
//
// Falls back to capturing the primary monitor (no crop, no occlusion pausing)
// when no game window is detected. NVENC only for now (no software fallback -
// machines without NVENC should stay on Legacy/OBS). Audio reuses
// AudioCapturePipeline - the same Game/Chat/Microphone routing, WASAPI capture, and mux
// logic WindowsReplayBuffer uses, via its own independent instance.
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class NativeReplayBuffer : IReplayBuffer
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly AudioCapturePipeline _audio;
    private readonly object _bufferLock = new();
    private readonly List<RingPacket> _packets = new();
    // Recording-paused transitions (see class summary) - trimmed alongside
    // _packets so this never grows unbounded across a long session.
    private readonly List<PauseEvent> _pauseEvents = new();

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

        // Captured once per session from the fresh encoder's SPS/PPS (see
        // CaptureLoop) - without resetting it here, a resolution change +
        // Restart Buffer opens a new encoder at the new size but keeps muxing
        // clips with the PREVIOUS session's stale extradata, which still
        // declares the old resolution. The container's declared size then
        // doesn't match the actual encoded frame data, producing exactly the
        // stride-mismatch smearing/corruption reported after a resolution
        // change.
        _extraData = null;
        lock (_bufferLock) _pauseEvents.Clear();

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
        lock (_bufferLock)
        {
            _packets.Clear();
            _pauseEvents.Clear();
        }
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

            WritePausedRangesSidecar(outputPath, ComputePausedRangesSeconds(GetOrderedPauseEvents(), windowStartUtc, windowStartUtc + TimeSpan.FromSeconds(windowDurationSeconds)));

            var tracks = await _audio.BuildAlignedTracksAsync(segmentWindows, config, snapshots, cancellationToken);

            var muxArgs = new List<string> { "-y", "-i", tempVideoPath };
            foreach (var track in tracks) muxArgs.AddRange(new[] { "-i", track.Path });
            muxArgs.AddRange(new[] { "-map", "0:v" });
            for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { "-map", $"{i + 1}:a" });
            muxArgs.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "192k" });
            for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { $"-metadata:s:a:{i}", $"title={tracks[i].Label}" });
            muxArgs.AddRange(new[] { "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("EVE Native")}", outputPath });
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
        IDXGIOutputDuplication? duplication = null;
        AVCodecContext* codecContext = null;
        SwsContext* swsContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        AVFormatContext* fullSessionFormatContext = null;
        AVStream* fullSessionStream = null;
        var fullSessionTempVideoPath = string.Empty;
        var fullSessionFinalOutputPath = string.Empty;
        var fullSessionStartUtc = DateTime.UtcNow;
        var timerResolutionRaised = TimeBeginPeriod(1) == 0;

        try
        {
            var config = _configProvider();
            device = CreateD3D11Device();

            var targetHandle = ResolveTargetWindow(config);
            var isMonitorMode = targetHandle == 0;
            duplication = CreateDuplicationFor(device, targetHandle, out var desktopBounds);

            var (captureWidth, captureHeight) = isMonitorMode
                ? (desktopBounds.Right - desktopBounds.Left, desktopBounds.Bottom - desktopBounds.Top)
                : GetInitialCropSize(targetHandle, desktopBounds);

            var (outputWidth, outputHeight) = CaptureOutputSize(config, captureWidth, captureHeight);
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;

            staging = CreateStagingTexture(device, captureWidth, captureHeight);

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
            // av_frame_get_buffer leaves the buffer uninitialized - if the
            // target window starts occluded (recording begins before the game
            // has focus, common when starting the buffer from EVE's own
            // window), the very first frames get encoded straight from that
            // garbage NV12 data before any real capture ever lands in it,
            // which renders as solid green (Y/U/V all near zero decodes to
            // bright green in YUV->RGB). Fill it to black up front instead.
            FillFrameBlack(frame, outputHeight);

            packet = ffmpeg.av_packet_alloc();

            AppLog.Info($"Native capture started (DXGI Desktop Duplication): target={(targetHandle != 0 ? "window" : "primary monitor")}, source={captureWidth}x{captureHeight}, output={outputWidth}x{outputHeight}, encoder=h264_nvenc, configFrameRate={config.FrameRate}.");
            ready.TrySetResult();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var lastForcedKeyframe = TimeSpan.Zero;
            var lastTargetRefresh = TimeSpan.Zero;
            var lastEncodedAt = TimeSpan.Zero;
            var targetFrameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(config.FrameRate, 15, 240));
            var lastDiagLog = TimeSpan.Zero;
            var lastRingTrim = TimeSpan.Zero;
            var framesSeen = 0;
            var framesEncoded = 0;
            var copyMapMs = 0.0;
            var scaleMs = 0.0;
            var encodeMs = 0.0;
            var framesEncodedSinceLog = 0;
            var stageStopwatch = new System.Diagnostics.Stopwatch();
            var getFrameMs = 0.0;
            var waitMs = 0.0;
            var iterationsSinceLog = 0;
            // Whether the target window is currently NOT foreground/visible - the
            // capture keeps encoding through this (re-submitting the last good
            // frame, see below) instead of stopping, so the ring buffer/full
            // session recording never has a real gap; SaveReplayAsync/
            // FinalizeFullSessionRecording read _pauseEvents to tell the editor
            // which parts of a saved clip were frozen like this.
            var isPaused = false;

            while (!token.IsCancellationRequested)
            {
                if (stopwatch.Elapsed - lastDiagLog >= TimeSpan.FromSeconds(2))
                {
                    lastDiagLog = stopwatch.Elapsed;
                    var n = Math.Max(1, framesEncodedSinceLog);
                    var m = Math.Max(1, iterationsSinceLog);
                    AppLog.Info($"Native capture diag: framesSeen={framesSeen}, framesEncoded={framesEncoded}, ringPackets={_packets.Count}, avgCopyMapMs={copyMapMs / n:0.00}, avgScaleMs={scaleMs / n:0.00}, avgEncodeMs={encodeMs / n:0.00}, avgWaitMs={waitMs / m:0.00}, avgGetFrameMs={getFrameMs / m:0.00}, iterations={iterationsSinceLog}.");
                    copyMapMs = 0;
                    scaleMs = 0;
                    encodeMs = 0;
                    framesEncodedSinceLog = 0;
                    waitMs = 0;
                    getFrameMs = 0;
                    iterationsSinceLog = 0;
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
                        isMonitorMode = targetHandle == 0;
                        duplication.Dispose();
                        try
                        {
                            duplication = CreateDuplicationFor(device, targetHandle, out desktopBounds);
                        }
                        catch (Exception error)
                        {
                            AppLog.Error("Native capture: failed to switch DXGI duplication target.", error);
                        }

                        if (isPaused)
                        {
                            isPaused = false;
                            lock (_bufferLock) _pauseEvents.Add(new PauseEvent(DateTime.UtcNow, false));
                        }
                    }
                }

                iterationsSinceLog++;
                stageStopwatch.Restart();
                var acquireResult = duplication.AcquireNextFrame(500, out _, out var desktopResource);
                waitMs += stageStopwatch.Elapsed.TotalMilliseconds;

                if (acquireResult.Failure)
                {
                    desktopResource?.Dispose();
                    if (acquireResult.Code == ResultCode.WaitTimeout.Code)
                    {
                        continue;
                    }

                    if (acquireResult.Code == ResultCode.AccessLost.Code)
                    {
                        AppLog.Info("Native capture: DXGI duplication access lost, recreating.");
                        duplication.Dispose();
                        try
                        {
                            duplication = CreateDuplicationFor(device, targetHandle, out desktopBounds);
                        }
                        catch (Exception error)
                        {
                            AppLog.Error("Native capture: failed to recreate DXGI duplication after access loss.", error);
                            Thread.Sleep(200);
                        }
                        continue;
                    }

                    // Transient failure (e.g. desktop switch) - brief backoff, retry.
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    framesSeen++;

                    stageStopwatch.Restart();
                    var occluded = !isMonitorMode && !IsWindowForegroundAndVisible(targetHandle);
                    int cropLeft = 0, cropTop = 0, cropWidth = captureWidth, cropHeight = captureHeight;
                    if (isMonitorMode)
                    {
                        cropWidth = desktopBounds.Right - desktopBounds.Left;
                        cropHeight = desktopBounds.Bottom - desktopBounds.Top;
                    }
                    else if (!occluded && !TryGetWindowCropRect(targetHandle, desktopBounds, out cropLeft, out cropTop, out cropWidth, out cropHeight))
                    {
                        // Window rect lookup failed transiently (e.g. mid-move/resize) -
                        // treat this frame as occluded/frozen instead of risking a bad copy.
                        occluded = true;
                        cropWidth = captureWidth;
                        cropHeight = captureHeight;
                    }
                    getFrameMs += stageStopwatch.Elapsed.TotalMilliseconds;

                    if (occluded != isPaused)
                    {
                        isPaused = occluded;
                        lock (_bufferLock) _pauseEvents.Add(new PauseEvent(DateTime.UtcNow, isPaused));
                        AppLog.Info($"Native capture: recording {(isPaused ? "paused (window not foreground)" : "resumed")}.");
                    }

                    if (!occluded && (cropWidth != captureWidth || cropHeight != captureHeight))
                    {
                        captureWidth = Math.Max(2, cropWidth);
                        captureHeight = Math.Max(2, cropHeight);
                        staging.Dispose();
                        staging = CreateStagingTexture(device, captureWidth, captureHeight);
                        ffmpeg.sws_freeContext(swsContext);
                        swsContext = CreateScaler(captureWidth, captureHeight, outputWidth, outputHeight);
                    }

                    if (stopwatch.Elapsed - lastEncodedAt < targetFrameInterval) continue;
                    lastEncodedAt = stopwatch.Elapsed;
                    framesEncoded++;
                    framesEncodedSinceLog++;

                    if (!occluded)
                    {
                        stageStopwatch.Restart();
                        using (var desktopTexture = desktopResource!.QueryInterface<ID3D11Texture2D>())
                        {
                            var box = new Vortice.Mathematics.Box(cropLeft, cropTop, 0, cropLeft + captureWidth, cropTop + captureHeight, 1);
                            device.ImmediateContext.CopySubresourceRegion(staging, 0, 0, 0, 0, desktopTexture, 0, box);
                        }

                        var mapped = device.ImmediateContext.Map(staging, 0, MapMode.Read, MapFlags.None);
                        copyMapMs += stageStopwatch.Elapsed.TotalMilliseconds;

                        stageStopwatch.Restart();
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
                        scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;
                    }
                    // else: occluded - frame->data still holds the last successfully
                    // scaled content, re-encoded unchanged below (visual freeze).

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

                    stageStopwatch.Restart();
                    if (ffmpeg.avcodec_send_frame(codecContext, frame) == 0)
                    {
                        DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream);
                    }
                    encodeMs += stageStopwatch.Elapsed.TotalMilliseconds;

                    if (stopwatch.Elapsed - lastRingTrim >= TimeSpan.FromSeconds(1))
                    {
                        lastRingTrim = stopwatch.Elapsed;
                        TrimRingBuffer();
                    }
                }
                finally
                {
                    duplication.ReleaseFrame();
                    desktopResource?.Dispose();
                }
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
            duplication?.Dispose();
            staging?.Dispose();
            device?.Dispose();
            if (timerResolutionRaised) TimeEndPeriod(1);
        }
    }

    private static unsafe void FillFrameBlack(AVFrame* frame, int height)
    {
        var ySize = (uint)(frame->linesize[0] * height);
        System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned((void*)frame->data[0], 16, ySize);

        var uvHeight = (height + 1) / 2;
        var uvSize = (uint)(frame->linesize[1] * uvHeight);
        System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned((void*)frame->data[1], 128, uvSize);
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

    // Finds the DXGI output covering whichever monitor the target window (or the
    // primary monitor, in monitor-capture mode) is on and duplicates it.
    // DesktopCoordinates comes back out so the caller can convert the window's
    // screen-space rect into texture-local crop coordinates every frame.
    private static IDXGIOutputDuplication CreateDuplicationFor(ID3D11Device device, nint targetHandle, out Vortice.RawRect desktopBounds)
    {
        var monitorHandle = targetHandle != 0
            ? MonitorFromWindow(targetHandle, MONITOR_DEFAULTTONEAREST)
            : GetPrimaryMonitorHandle();

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();

        for (uint i = 0; ; i++)
        {
            var enumResult = adapter.EnumOutputs(i, out var output);
            if (enumResult.Failure) break;

            using (output)
            {
                if (output.Description.Monitor != monitorHandle) continue;

                using var output1 = output.QueryInterface<IDXGIOutput1>();
                desktopBounds = output.Description.DesktopCoordinates;
                return output1.DuplicateOutput(device);
            }
        }

        throw new InvalidOperationException("No DXGI output found for the target monitor.");
    }

    // Only used once at loop startup (the crop rect is recomputed every frame
    // afterward) - falls back to the full monitor if the window rect can't be
    // read yet (e.g. window still opening), self-corrects on the next frame.
    private static (int Width, int Height) GetInitialCropSize(nint targetHandle, Vortice.RawRect desktopBounds)
    {
        if (TryGetWindowCropRect(targetHandle, desktopBounds, out _, out _, out var width, out var height))
        {
            return (width, height);
        }

        return (desktopBounds.Right - desktopBounds.Left, desktopBounds.Bottom - desktopBounds.Top);
    }

    // Converts the target window's current screen-space rect into coordinates
    // local to the duplicated desktop texture (which starts at desktopBounds'
    // top-left, not (0,0) on multi-monitor setups), clipped to the monitor.
    // DWMWA_EXTENDED_FRAME_BOUNDS is the window's actual visible bounds
    // (excludes the invisible resize-shadow margin GetWindowRect includes on
    // Windows 10/11), falling back to GetWindowRect if unavailable.
    private static bool TryGetWindowCropRect(nint handle, Vortice.RawRect desktopBounds, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        if (!IsWindow(handle) || IsIconic(handle)) return false;

        if (DwmGetWindowAttribute(handle, DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<RectStruct>()) != 0)
        {
            if (!GetWindowRect(handle, out rect)) return false;
        }

        var clipLeft = Math.Max(rect.Left, desktopBounds.Left);
        var clipTop = Math.Max(rect.Top, desktopBounds.Top);
        var clipRight = Math.Min(rect.Right, desktopBounds.Right);
        var clipBottom = Math.Min(rect.Bottom, desktopBounds.Bottom);
        if (clipRight <= clipLeft || clipBottom <= clipTop) return false;

        left = clipLeft - desktopBounds.Left;
        top = clipTop - desktopBounds.Top;
        width = clipRight - clipLeft;
        height = clipBottom - clipTop;
        return true;
    }

    // "Foreground" (not just "visible") is the deliberate bar here - a window
    // can be fully visible on a second monitor while the user is alt-tabbed
    // into something covering the game's own monitor, and DXGI Desktop
    // Duplication captures per-monitor composited output either way, so
    // foreground is the only reliable "nothing else could be leaking into this
    // frame" signal available without walking the full z-order.
    private static bool IsWindowForegroundAndVisible(nint handle) =>
        IsWindow(handle) && !IsIconic(handle) && GetForegroundWindow() == handle;

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
            var sessionEndUtc = DateTime.UtcNow;
            var durationSeconds = Math.Max(1, (sessionEndUtc - sessionStartUtc).TotalSeconds);
            WritePausedRangesSidecar(finalOutputPath, ComputePausedRangesSeconds(GetOrderedPauseEvents(), sessionStartUtc, sessionEndUtc));
            // One giant segment spanning the whole session let audio/video clock
            // drift (real hardware sample clocks are never exactly 48000.000000Hz)
            // accumulate uncorrected for the entire recording - fine for the first
            // minute or two, audibly desynced well before a long session ends.
            // Regular replay clips never hit this because WindowsReplayBuffer
            // segments and independently re-anchors audio every ~60s; chunking the
            // session the same way here gets the same periodic resync instead of
            // one uncorrected multi-hour window.
            const double SegmentChunkSeconds = 60;
            var segmentWindows = new List<(DateTime StartUtc, double DurationSeconds)>();
            var chunkStartUtc = sessionStartUtc;
            var remainingSeconds = durationSeconds;
            while (remainingSeconds > 0)
            {
                var chunkSeconds = Math.Min(SegmentChunkSeconds, remainingSeconds);
                segmentWindows.Add((chunkStartUtc, chunkSeconds));
                chunkStartUtc += TimeSpan.FromSeconds(chunkSeconds);
                remainingSeconds -= chunkSeconds;
            }

            var tracks = _audio
                .BuildAlignedTracksAsync(segmentWindows, config, snapshots, CancellationToken.None)
                .GetAwaiter().GetResult();

            var muxArgs = new List<string> { "-y", "-i", tempVideoPath };
            foreach (var track in tracks) muxArgs.AddRange(new[] { "-i", track.Path });
            muxArgs.AddRange(new[] { "-map", "0:v" });
            for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { "-map", $"{i + 1}:a" });
            muxArgs.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "192k" });
            for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { $"-metadata:s:a:{i}", $"title={tracks[i].Label}" });
            muxArgs.AddRange(new[] { "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("EVE Native Full Session")}", finalOutputPath });
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

            // Keeps at most one event before the cutoff (needed so
            // ComputePausedRangesSeconds can still tell what state a save
            // window started in) and drops everything older than that.
            var keepFromIndex = 0;
            for (var i = _pauseEvents.Count - 1; i >= 0; i--)
            {
                if (_pauseEvents[i].WallClockUtc < cutoff) { keepFromIndex = i; break; }
            }
            if (keepFromIndex > 0) _pauseEvents.RemoveRange(0, keepFromIndex);
        }
    }

    private PauseEvent[] GetOrderedPauseEvents()
    {
        lock (_bufferLock) return _pauseEvents.OrderBy(e => e.WallClockUtc).ToArray();
    }

    // Reconstructs the paused/frozen (game window not foreground during DXGI
    // Desktop Duplication capture - see class summary) time ranges that fall
    // within [windowStartUtc, windowEndUtc), as offsets in seconds from the
    // window's start, for the "Recording Paused" editor overlay to read.
    private static List<(double StartSeconds, double EndSeconds)> ComputePausedRangesSeconds(
        PauseEvent[] orderedEvents, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        var currentlyPaused = false;
        foreach (var e in orderedEvents)
        {
            if (e.WallClockUtc > windowStartUtc) break;
            currentlyPaused = e.IsPaused;
        }

        var ranges = new List<(double, double)>();
        var pauseStartUtc = currentlyPaused ? windowStartUtc : (DateTime?)null;

        foreach (var e in orderedEvents)
        {
            if (e.WallClockUtc <= windowStartUtc || e.WallClockUtc >= windowEndUtc) continue;
            if (e.IsPaused == currentlyPaused) continue;

            if (e.IsPaused)
            {
                pauseStartUtc = e.WallClockUtc;
            }
            else if (pauseStartUtc is not null)
            {
                ranges.Add((
                    Math.Max(0, (pauseStartUtc.Value - windowStartUtc).TotalSeconds),
                    (e.WallClockUtc - windowStartUtc).TotalSeconds));
                pauseStartUtc = null;
            }

            currentlyPaused = e.IsPaused;
        }

        if (currentlyPaused && pauseStartUtc is not null)
        {
            ranges.Add((
                Math.Max(0, (pauseStartUtc.Value - windowStartUtc).TotalSeconds),
                (windowEndUtc - windowStartUtc).TotalSeconds));
        }

        return ranges;
    }

    private static void WritePausedRangesSidecar(string outputPath, List<(double StartSeconds, double EndSeconds)> ranges)
    {
        if (ranges.Count == 0) return;
        try
        {
            var payload = ranges.Select(r => new { start = Math.Round(r.StartSeconds, 2), end = Math.Round(r.EndSeconds, 2) }).ToArray();
            File.WriteAllText(outputPath + ".paused.json", JsonSerializer.Serialize(payload));
        }
        catch (Exception error)
        {
            AppLog.Error("Failed to write recording-paused sidecar.", error);
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

        // Microsoft's own WGC samples explicitly mark the D3D11 device
        // multithread-protected when it's touched from both the capture
        // pool's internal thread and a consumer thread (exactly our setup -
        // WGC's own frame production vs. our capture loop's CopyResource/Map
        // calls on the same device). Without it, the driver can apply
        // conservative cross-thread synchronization that serializes/throttles
        // access - a plausible source of a fixed-ish fps ceiling that nothing
        // on the consumption side (buffer depth, event vs. polling, timer
        // resolution) could touch, since none of those affect device-level
        // thread safety. Never tried before now; safe no-op if unsupported.
        TryMarkDeviceMultithreadProtected(device!);
        return device!;
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int SetMultithreadProtectedDelegate(IntPtr self, int bMTProtect);

    // Vortice's ComObject.QueryInterface<T>() requires T to be a SharpGen-generated
    // ComObject, which ID3D10Multithread isn't (Vortice.Direct3D11 doesn't define
    // it) - falls back to a raw COM QueryInterface + manual vtable call instead.
    // ID3D10Multithread's vtable, after IUnknown's QueryInterface/AddRef/Release
    // (indices 0-2): Enter=3, Leave=4, SetMultithreadProtected=5, GetMultithreadProtected=6.
    private static void TryMarkDeviceMultithreadProtected(ID3D11Device device)
    {
        var multithreadIid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
        var multithreadPtr = IntPtr.Zero;
        try
        {
            var hr = Marshal.QueryInterface(device.NativePointer, ref multithreadIid, out multithreadPtr);
            if (hr != 0 || multithreadPtr == IntPtr.Zero)
            {
                AppLog.Info($"Native capture: D3D11 device does not support ID3D10Multithread (hr={hr}), continuing without it.");
                return;
            }

            var vtable = Marshal.ReadIntPtr(multithreadPtr, 0);
            var setMultithreadProtectedPtr = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);
            var setMultithreadProtected = Marshal.GetDelegateForFunctionPointer<SetMultithreadProtectedDelegate>(setMultithreadProtectedPtr);
            setMultithreadProtected(multithreadPtr, 1);
            AppLog.Info("Native capture: D3D11 device marked multithread-protected.");
        }
        catch (Exception error)
        {
            AppLog.Info($"Native capture: could not mark D3D11 device multithread-protected (non-fatal): {error.Message}");
        }
        finally
        {
            if (multithreadPtr != IntPtr.Zero) Marshal.Release(multithreadPtr);
        }
    }

    // Windows' default system timer resolution is ~15.6ms unless a process
    // explicitly requests better - Thread.Sleep(4) in CaptureLoop's empty-poll
    // fallback commonly actually sleeps ~15.6ms on an unraised system (rounds
    // up to the next scheduler tick), not 4ms. That's a hard, resolution-
    // independent ceiling of ~64 loop iterations/sec regardless of encode
    // settings - consistent with what testing showed (same ~35-46fps whether
    // targeting 1080p60 or 1440p120, with the encode/scale/copy stages
    // themselves proven to have headroom for 80fps+). Raising it to 1ms for
    // the life of the capture session is the standard fix for latency-
    // sensitive polling loops on Windows (the same thing games/capture
    // software normally do).
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointStruct pt, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RectStruct lpRect);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RectStruct pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    private readonly record struct RingPacket(byte[] Data, long PtsMs, bool IsKeyframe, DateTime WallClockUtc);

    private readonly record struct PauseEvent(DateTime WallClockUtc, bool IsPaused);
}
