using Eve.Capture.Abstractions;
using FFmpeg.AutoGen;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;
using ID3D11VideoContext = Vortice.Direct3D11.ID3D11VideoContext;
using ResultCode = Vortice.DXGI.ResultCode;
using MapFlags = Vortice.Direct3D11.MapFlags;
using CpuAccessFlags = Vortice.Direct3D11.CpuAccessFlags;
using BindFlags = Vortice.Direct3D11.BindFlags;
using ResourceUsage = Vortice.Direct3D11.ResourceUsage;
using MapMode = Vortice.Direct3D11.MapMode;
using Texture2DDescription = Vortice.Direct3D11.Texture2DDescription;
using D3D11 = Vortice.Direct3D11.D3D11;
using DeviceCreationFlags = Vortice.Direct3D11.DeviceCreationFlags;

namespace Eve.App.Services;

// Native capture engine: direct h264_nvenc/amf/libx264 encode via libavcodec
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
    private readonly string _bufferFolder;
    private readonly AudioCapturePipeline _audio;
    private readonly object _bufferLock = new();
    private readonly List<RingPacket> _packets = new();
    // Recording-paused transitions (see class summary) - trimmed alongside
    // _packets so this never grows unbounded across a long session.
    private readonly List<PauseEvent> _pauseEvents = new();

    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private Task? _backgroundFinalize;
    // Guards StartAsync's orphan-WAV sweep across the app: a background
    // finalize still owns capture WAVs after its session stopped, and a new
    // session starting meanwhile must not sweep them out from under it.
    private static int _activeBackgroundFinalizes;
    private volatile bool _sessionActive;
    private AVRational _timeBase = new() { num = 1, den = 1_000_000 };
    private byte[]? _extraData;
    private int _outputWidth;
    private int _outputHeight;
    // Encode-thread diagnostics (see EncodeLoop) - written with Interlocked from
    // that thread, read/reset from CaptureLoop's own periodic diag line. Plain
    // instance fields are safe here since only one capture session (and so only
    // one encode thread) is ever active at a time.
    private long _encodeMicrosAccum;
    private long _encodeCountAccum;
    private long _encodeDroppedCount;

    public NativeReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
        _bufferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "native-replay-buffer");
        _audio = new AudioCapturePipeline(_bufferFolder);
    }

    public bool IsRecording => _sessionActive;
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);
    public event EventHandler? RecordingStopped;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionActive) return Task.CompletedTask;

        Directory.CreateDirectory(_bufferFolder);
        if (Volatile.Read(ref _activeBackgroundFinalizes) == 0)
        {
            CleanupOldFiles();
        }
        else
        {
            AppLog.Info("Native replay start: skipping orphan-WAV sweep, a background session finalize still owns capture files.");
        }

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

    // Sweeps orphaned raw audio WAVs left behind by an ungraceful shutdown
    // (crash, Task Manager kill, power loss) - the normal Stop()/PruneOlderThan
    // paths delete these as they go, but neither runs if the process never got
    // to call them. Safe to run unconditionally at StartAsync: nothing is
    // actively writing to this folder yet since no capture has started.
    private void CleanupOldFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "game_*.wav")
                     .Concat(Directory.EnumerateFiles(_bufferFolder, "chat_*.wav"))
                     .Concat(Directory.EnumerateFiles(_bufferFolder, "microphone_*.wav"))
                     .Concat(Directory.EnumerateFiles(_bufferFolder, "audio_*.wav"))
                     .Concat(Directory.EnumerateFiles(_bufferFolder, "audio_*.txt")))
        {
            AudioCapturePipeline.TryDelete(file);
        }
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

        // When a background finalize is running it took a snapshot of the
        // capture set and deletes the WAVs itself once the session file is
        // complete - deleting them here would yank them out from under it.
        _audio.Stop(deleteCaptureFiles: _backgroundFinalize is null || _backgroundFinalize.IsCompleted);
        lock (_bufferLock)
        {
            _packets.Clear();
            _pauseEvents.Clear();
        }
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null)
    {
        if (!_sessionActive) throw new InvalidOperationException("Replay buffer is not recording.");

        RingPacket[] window;
        lock (_bufferLock)
        {
            if (_packets.Count == 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

            var requestedStartUtc = clipWindow?.StartUtc ?? MonotonicClock.UtcNow - Duration;
            var requestedEndUtc = clipWindow?.EndUtc ?? MonotonicClock.UtcNow;
            if (requestedEndUtc <= requestedStartUtc)
            {
                throw new InvalidOperationException("The requested replay window is empty.");
            }

            // Saving from a keyframe immediately before the requested event keeps
            // the remux playable while still producing an event-sized clip.
            var cutoffUtc = requestedStartUtc;
            var startIndex = _packets.FindLastIndex(p => p.WallClockUtc <= cutoffUtc && p.IsKeyframe);
            if (startIndex < 0) startIndex = _packets.FindIndex(p => p.IsKeyframe);
            if (startIndex < 0) throw new InvalidOperationException("Replay just started. Try again in a second.");
            var endIndex = _packets.FindLastIndex(p => p.WallClockUtc <= requestedEndUtc);
            if (endIndex < startIndex) throw new InvalidOperationException("The requested replay window is no longer available.");

            window = _packets.Skip(startIndex).Take(endIndex - startIndex + 1).ToArray();
        }

        if (window.Length == 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

        var config = _configProvider();
        var clipName = string.IsNullOrWhiteSpace(titleOverride) ? config.GameDisplayName : titleOverride;
        var gameFolder = Path.Combine(outputFolder, ClipFileNaming.BuildBaseName(config.GameDisplayName));
        Directory.CreateDirectory(gameFolder);
        var outputPath = ClipFileNaming.BuildUniquePath(gameFolder, ClipFileNaming.BuildFileName(clipName, DateTime.Now, "mp4", config.ClipFileNameScheme, config.CustomClipFileNameTemplate, config.GameDisplayName));

        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"eve-native-video-{Guid.NewGuid():N}.mp4");
        var snapshots = new List<string>();
        try
        {
            await Task.Run(() => RemuxWindowToMp4(window, tempVideoPath), cancellationToken);

            // The ring buffer already remuxes exactly the desired window starting at a
            // real keyframe - no offset/trim needed here the way WindowsReplayBuffer's
            // keyframe-seek fallback requires.
            var windowStartUtc = window[0].WallClockUtc;
            var windowDurationSeconds = Math.Max(1, (window[^1].WallClockUtc - windowStartUtc).TotalSeconds);

            // Diagnostic only (see audio-desync investigation) - video's own
            // internal duration comes from Stopwatch-based PTS (monotonic,
            // high precision), while the audio segment above is sized from
            // wall-clock (DateTime.UtcNow) deltas between the same two
            // packets. If these disagree by more than a few ms, the audio
            // track gets built to a different total length than the video
            // actually has, which wouldn't just be a start offset - it'd get
            // worse toward the end of the clip.
            var videoDurationSeconds = (window[^1].PtsMs - window[0].PtsMs) / 1_000_000.0;
            AppLog.Debug($"Native replay audio/video duration check: videoDurationSeconds={videoDurationSeconds:0.000}, audioWindowDurationSeconds={windowDurationSeconds:0.000}, deltaMs={(windowDurationSeconds - videoDurationSeconds) * 1000:0.0}, packetCount={window.Length}.");

            // A capture stall (the loop goes an extended stretch without
            // acquiring/encoding a frame - seen under heavy GPU load, driver
            // hiccups, etc.) leaves real gaps in the ring buffer: fewer video
            // packets than wall-clock time would suggest, while audio (an
            // entirely separate capture pipeline) keeps recording the whole
            // window regardless. Left uncorrected, the saved clip gets an
            // audio track much longer than its video track, which plays back
            // as "video freezes, audio keeps going" for however long the
            // shortfall is. Trimming the audio window down to the real video
            // length (keeping the END - the moment closest to the save
            // request - and cutting from the front) turns that into a
            // shorter but correctly synced clip instead.
            if (windowDurationSeconds - videoDurationSeconds > 1.0)
            {
                AppLog.Info($"Native replay: video came up short ({videoDurationSeconds:0.0}s of {windowDurationSeconds:0.0}s requested, likely a capture stall) - trimming audio to match.");
                windowStartUtc = window[^1].WallClockUtc - TimeSpan.FromSeconds(videoDurationSeconds);
                windowDurationSeconds = videoDurationSeconds;
            }

            // One giant segment spanning the whole saved window let audio/video
            // clock drift (real hardware sample clocks are never exactly
            // 48000.000000Hz) accumulate uncorrected across the entire clip -
            // FinalizeFullSessionRecording already chunks its (much longer)
            // window into 60s segments with a periodic resync for exactly this
            // reason, but a regular clip save at the default 60s replay length
            // is long enough to hit the same drift, just less obviously since
            // it's usually the ONLY segment. Chunking here the same way fixes it
            // for any configured replay length, not just multi-hour sessions.
            const double SegmentChunkSeconds = 60;
            var segmentWindows = new List<(DateTime StartUtc, double DurationSeconds)>();
            // Positive AudioSyncOffsetMs pulls the audio SOURCE window earlier
            // in real time (while keeping each segment's requested duration
            // the same) - the resulting output audio then plays content that
            // was actually captured slightly earlier at the same point in the
            // timeline, which is what "audio sounds delayed relative to
            // video" needs. Deliberately only shifts the audio side - video's
            // own PTS/paused-ranges timeline is untouched.
            var chunkStartUtc = windowStartUtc - TimeSpan.FromMilliseconds(config.AudioSyncOffsetMs);
            var remainingSeconds = windowDurationSeconds;
            while (remainingSeconds > 0)
            {
                var chunkSeconds = Math.Min(SegmentChunkSeconds, remainingSeconds);
                segmentWindows.Add((chunkStartUtc, chunkSeconds));
                chunkStartUtc += TimeSpan.FromSeconds(chunkSeconds);
                remainingSeconds -= chunkSeconds;
            }

            WritePausedRangesSidecar(config.LibraryFolder, outputPath, ComputePausedRangesSeconds(GetOrderedPauseEvents(), windowStartUtc, windowStartUtc + TimeSpan.FromSeconds(windowDurationSeconds)));

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
        // Every save (auto or manual) runs RemuxWindowToMp4 - a synchronous,
        // single-threaded loop of thousands of native FFmpeg calls writing
        // straight to disk - on a plain Task.Run threadpool thread at Normal
        // priority, same as this loop. On a system without much CPU headroom,
        // that's enough to starve this thread of scheduling for a second or
        // more at a time: per-frame GPU/encode costs stay normal during a
        // stall (see Native capture diag's avgScaleMs/avgEncodeMs), but the
        // loop's own iteration rate collapses, which is what "video freezes/
        // stutters right when a clip saves - even via the manual hotkey"
        // turned out to be. AboveNormal (not Highest, which risks starving
        // the OS's own threads if held for this thread's entire multi-hour
        // capture-session lifetime) gives the scheduler a reason to favor
        // this over that remux work specifically during the contention.
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }
        catch (Exception error)
        {
            AppLog.Error("Native capture: failed to raise capture thread priority (non-fatal)", error);
        }

        ID3D11Device? device = null;
        ID3D11Texture2D? staging = null;
        IDXGIOutputDuplication? duplication = null;
        // GPU-side downscale path (see TrySetupGpuScale) - only actually used
        // when useGpuScale ends up true; `staging`/swsContext above always
        // still get created too so there's a guaranteed-working fallback if
        // GPU scale setup fails on this hardware/driver.
        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext? videoContext = null;
        ID3D11VideoProcessorEnumerator? vpEnumerator = null;
        ID3D11VideoProcessor? videoProcessor = null;
        ID3D11Texture2D? croppedTexture = null;
        ID3D11Texture2D? nv12Output = null;
        // Two staging textures instead of one, alternated each frame: Map()
        // without DoNotWait blocks until the GPU finishes EVERYTHING queued
        // before it, and mapping the texture we just issued a CopyResource
        // into forces exactly that stall (measured 12-14ms, wildly variable
        // with GPU load - defeating much of the point of GPU scale). Copying
        // into one slot while mapping the OTHER slot (whose copy had a full
        // iteration's worth of async time to actually finish) avoids the
        // stall entirely - standard double-buffered GPU readback.
        ID3D11Texture2D[]? nv12StagingRing = null;
        var nv12StagingIndex = 0;
        var nv12RingPrimed = false;
        ID3D11VideoProcessorOutputView? outputView = null;
        ID3D11VideoProcessorInputView? inputView = null;
        var useGpuScale = false;
        AVCodecContext* codecContext = null;
        SwsContext* swsContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        AVFormatContext* fullSessionFormatContext = null;
        AVStream* fullSessionStream = null;
        // Encode (avcodec_send_frame/receive_packet, both of which can block on
        // NVENC for a while under real GPU contention - see EncodeLoop) runs on
        // its own thread so a slow encode call never blocks AcquireNextFrame on
        // the thread below it. Declared out here (not inside the try) so the
        // finally block can always drain/join it before codecContext gets freed,
        // on every exit path including an exception mid-loop.
        BlockingCollection<EncodeJob>? encodeQueue = null;
        Thread? encodeThread = null;
        var fullSessionTempVideoPath = string.Empty;
        var fullSessionFinalOutputPath = string.Empty;
        var fullSessionStartUtc = MonotonicClock.UtcNow;
        // Real wall-clock twin of fullSessionStartUtc, only for the sidecar's
        // user-facing CreatedAt - all alignment math stays on MonotonicClock.
        var fullSessionStartWallUtc = DateTime.UtcNow;
        var fullSessionGameDisplayName = string.Empty;
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

            // Copying+CPU-scaling the full captured crop (often the game's
            // native 4K render size) down to the output resolution every
            // frame measured ~17-18ms/frame by itself - most of a 60fps
            // frame budget - and is what's actually capping fps now that
            // Desktop Duplication itself isn't the bottleneck anymore.
            // Scaling on the GPU instead means only the already-small output
            // resolution ever gets read back to the CPU. Best-effort: if
            // this hardware/driver doesn't support it, useGpuScale stays
            // false and the CPU sws_scale path above still works normally.
            try
            {
                (videoDevice, videoContext, vpEnumerator, videoProcessor, nv12Output, nv12StagingRing, outputView) =
                    CreateGpuScaler(device, captureWidth, captureHeight, outputWidth, outputHeight, config.FrameRate);
                (croppedTexture, inputView) = CreateGpuCropInputView(device, videoDevice, vpEnumerator, captureWidth, captureHeight);
                useGpuScale = true;
                AppLog.Info("Native capture: GPU downscale (D3D11 Video Processor) available, using it.");
            }
            catch (Exception error)
            {
                AppLog.Info($"Native capture: GPU downscale unavailable, falling back to CPU scale: {error.Message}");
                useGpuScale = false;
            }

            codecContext = CreateEncoder(config, outputWidth, outputHeight, out var codecTimeBase, out var encoderName);
            _timeBase = codecTimeBase;

            if (InitFullSessionWriter(config, codecContext, out fullSessionFormatContext, out fullSessionStream, out fullSessionTempVideoPath, out fullSessionFinalOutputPath))
            {
                fullSessionStartUtc = MonotonicClock.UtcNow;
                fullSessionStartWallUtc = DateTime.UtcNow;
                fullSessionGameDisplayName = config.GameDisplayName;
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

            // Bounded generously past a worst-case single-iteration catch-up burst
            // (see the pacing gate below, capped at FrameRate*2 duplicate-encoded
            // frames) so a legitimate burst never spuriously drops frames - only a
            // genuinely sustained backlog (the encoder truly can't keep pace, not
            // just a transient stall) hits the cap and starts dropping in
            // EncodeLoop's TryAdd below.
            encodeQueue = new BlockingCollection<EncodeJob>(boundedCapacity: Math.Max(64, Math.Clamp(config.FrameRate, 15, 240) * 2));
            // Pointer locals can't be captured by a lambda closure directly - cross
            // the thread boundary as nint instead, cast back inside EncodeLoop.
            var encodeCodecContextPtr = (nint)codecContext;
            var encodePacketPtr = (nint)packet;
            var encodeFullSessionFormatContextPtr = (nint)fullSessionFormatContext;
            var encodeFullSessionStreamPtr = (nint)fullSessionStream;
            encodeThread = new Thread(() => EncodeLoop(encodeQueue, encodeCodecContextPtr, encodePacketPtr, encodeFullSessionFormatContextPtr, encodeFullSessionStreamPtr))
            {
                IsBackground = true,
                Name = "EVE-NativeEncode"
            };
            try { encodeThread.Priority = ThreadPriority.AboveNormal; }
            catch (Exception error) { AppLog.Error("Native capture: failed to raise encode thread priority (non-fatal)", error); }
            encodeThread.Start();

            AppLog.Info($"Native capture started (DXGI Desktop Duplication): target={(targetHandle != 0 ? "window" : "primary monitor")}, source={captureWidth}x{captureHeight}, output={outputWidth}x{outputHeight}, encoder={encoderName}, configFrameRate={config.FrameRate}.");
            ready.TrySetResult();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Anchors packet->pts (a Stopwatch-based, accurate-to-the-real-
            // capture-moment value) back to a real wall-clock instant, so
            var lastForcedKeyframe = TimeSpan.Zero;
            var lastTargetRefresh = TimeSpan.Zero;
            var lastEncodedAt = TimeSpan.Zero;
            var targetFrameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(config.FrameRate, 15, 240));
            // Counts encoded frames (including duplicate/padding ones) so
            // frame->pts can be assigned an IDEAL, constant-rate timestamp
            // (index * exact interval) rather than real elapsed time - see
            // the pacing gate below for why. RingPacket.WallClockUtc (used
            // only for audio alignment) is tracked completely separately via
            // a MonotonicClock.UtcNow captured at each actual encode, passed
            // straight into DrainToRingBuffer, so idealizing video's own
            // timeline can't drag audio sync off with it.
            var encodedFrameIndex = 0L;
            var idealFrameIntervalMicroseconds = 1_000_000.0 / Math.Clamp(config.FrameRate, 15, 240);
            // The real capture-moment timestamp FIFO (one per avcodec_send_frame
            // call, dequeued one-for-one as packets actually come out - see
            // DrainToRingBuffer for why this, not just "now" at drain time, is
            // what fixes audio sync) now lives inside EncodeLoop, since send_frame
            // itself moved there - see the encode-queue enqueue below.
            // Short enough to stay well under even a 240fps target interval
            // (4.17ms) so the pacing gate below is never blocked waiting on
            // this call - see its call site for why that matters now. Lower
            // than this measured no further benefit and just adds pure
            // syscall/COM-marshaling overhead from calling AcquireNextFrame
            // more often for no timing gain.
            const uint AcquireTimeoutMs = 1;
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
            // AcquireNextFrame's OutduplFrameInfo was previously discarded
            // entirely (`out _`) - LastPresentTime==0 specifically means the
            // desktop IMAGE didn't actually change (e.g. only the OS cursor
            // moved), and AccumulatedFrames>1 means the OS coalesced more
            // than one real present into this single delivery because we
            // weren't keeping up. Both were being silently treated as "a new
            // real frame" before, which could burn pacing-gate slots on
            // duplicate content instead of genuinely new ones. Tracked here
            // to find out which is actually happening on this hardware
            // instead of continuing to guess.
            var zeroPresentSkips = 0;
            var accumulatedFramesSum = 0L;
            var accumulatedFramesMax = 0L;
            var lastRealPresentTicks = 0L;
            var presentGapSumMs = 0.0;
            var presentGapCount = 0;
            var presentGapMaxMs = 0.0;
            // Diagnostic for the "video freezes/stutters, but avgCopyMapMs/
            // avgScaleMs/avgEncodeMs/avgWaitMs/avgGetFrameMs all stay normal"
            // investigation - none of those named stages cover the top of the
            // loop (diag logging, the once-a-second ResolveTargetWindow
            // recheck and its DXGI duplication recreate if the target
            // changed, the null-duplication retry path). A real stall in a
            // region no stopwatch was watching would be invisible in every
            // other metric while iterations/sec still collapsed - which is
            // exactly what the logs showed. This measures everything between
            // the end of one iteration's work and the start of the next
            // AcquireNextFrame call, whatever it turns out to be.
            var preAcquireStopwatch = new System.Diagnostics.Stopwatch();
            var preAcquireMs = 0.0;
            var preAcquireMaxMs = 0.0;
            preAcquireStopwatch.Start();
            var gen2CountAtLastIteration = GC.CollectionCount(2);
            var gen1CountAtLastIteration = GC.CollectionCount(1);
            // Whether the target window is currently NOT foreground/visible - the
            // capture keeps encoding through this (re-submitting the last good
            // frame, see below) instead of stopping, so the ring buffer/full
            // session recording never has a real gap; SaveReplayAsync/
            // FinalizeFullSessionRecording read _pauseEvents to tell the editor
            // which parts of a saved clip were frozen like this.
            var isPaused = false;
            // Whether a real (non-occluded) frame has ever been captured yet.
            // The buffer arms the instant a game is detected, which is often
            // before the game window has focus (user alt-tabbed away, or just
            // hasn't clicked in yet) - frame->data is still FillFrameBlack's
            // placeholder at that point. Encoding used to start immediately
            // regardless, so the ring buffer/full session opened on however
            // many seconds of solid black preceded the user actually looking
            // at the game. Gating the encode/ring-write step on this instead
            // means that stretch is simply never recorded - once the window
            // has been seen in the foreground once, later occlusions (a
            // mid-session alt-tab) go back to the existing freeze-and-keep-
            // recording behavior above, unaffected.
            var hasCapturedRealFrame = false;

            while (!token.IsCancellationRequested)
            {
                if (stopwatch.Elapsed - lastDiagLog >= TimeSpan.FromSeconds(2))
                {
                    lastDiagLog = stopwatch.Elapsed;
                    var n = Math.Max(1, framesEncodedSinceLog);
                    var m = Math.Max(1, iterationsSinceLog);
                    var realFrameCount = Math.Max(1, iterationsSinceLog - zeroPresentSkips);
                    var presentGapDenom = Math.Max(1, presentGapCount);
                    // gen2Count/managedMb: if a stall coincides with gen2Count
                    // actually incrementing between two diag lines (or between
                    // the before/after read on a single spike below), that's
                    // a blocking GC pause, not a GPU/DXGI/scheduling stall -
                    // a multi-GB managed heap (see managedMb) can produce
                    // multi-second Gen2/full collections that freeze every
                    // managed thread at once, including this one, which would
                    // explain gaps this large with every named stage timer
                    // (GPU copy/scale/encode, AcquireNextFrame itself) still
                    // reading normal.
                    var managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
                    // Distinguishes "the video ring buffer itself is what's
                    // ballooning" from "something else is" (the audio
                    // capture side - AudioCapturePipeline - runs on its own
                    // 2s trim timer, completely independent of this loop, and
                    // is the other obvious candidate for runaway MemoryStream
                    // growth). If ringBufferMb tracks managedMb closely, the
                    // problem is in this ring buffer/packet handling; if
                    // managedMb spikes far above ringBufferMb, it's elsewhere.
                    long ringBufferBytes;
                    lock (_bufferLock) ringBufferBytes = _packets.Sum(p => (long)p.Data.Length);
                    var ringBufferMb = ringBufferBytes / (1024 * 1024);
                    // avgEncodeMs now comes from EncodeLoop's own thread (Interlocked
                    // handoff, reset via Exchange so nothing's lost mid-read) - the
                    // capture-thread-local encodeMs is relabeled avgQueueMs, since
                    // it's now just av_frame_clone+TryAdd cost, not the real encode
                    // call. queueDepth/droppedFrames confirm whether decoupling is
                    // actually keeping up: a growing depth or nonzero drops under
                    // load means the encoder itself is too slow, not just blocked.
                    var encodeCountSinceLog = Math.Max(1, Interlocked.Exchange(ref _encodeCountAccum, 0));
                    var encodeMicrosSinceLog = Interlocked.Exchange(ref _encodeMicrosAccum, 0);
                    var droppedSinceLog = Interlocked.Exchange(ref _encodeDroppedCount, 0);
                    AppLog.Debug($"Native capture diag: framesSeen={framesSeen}, framesEncoded={framesEncoded}, ringPackets={_packets.Count}, ringBufferMb={ringBufferMb}, avgCopyMapMs={copyMapMs / n:0.00}, avgScaleMs={scaleMs / n:0.00}, avgQueueMs={encodeMs / n:0.00}, avgEncodeMs={encodeMicrosSinceLog / 1000.0 / encodeCountSinceLog:0.00}, queueDepth={encodeQueue.Count}, droppedFrames={droppedSinceLog}, avgWaitMs={waitMs / m:0.00}, avgGetFrameMs={getFrameMs / m:0.00}, avgPreAcquireMs={preAcquireMs / m:0.00}, maxPreAcquireMs={preAcquireMaxMs:0.00}, iterations={iterationsSinceLog}, zeroPresentSkips={zeroPresentSkips}, avgAccumulatedFrames={(double)accumulatedFramesSum / realFrameCount:0.00}, maxAccumulatedFrames={accumulatedFramesMax}, avgPresentGapMs={presentGapSumMs / presentGapDenom:0.00}, maxPresentGapMs={presentGapMaxMs:0.00}, managedMb={managedMb}, gen0={GC.CollectionCount(0)}, gen1={GC.CollectionCount(1)}, gen2={GC.CollectionCount(2)}.");
                    copyMapMs = 0;
                    scaleMs = 0;
                    encodeMs = 0;
                    framesEncodedSinceLog = 0;
                    waitMs = 0;
                    getFrameMs = 0;
                    iterationsSinceLog = 0;
                    zeroPresentSkips = 0;
                    accumulatedFramesSum = 0;
                    accumulatedFramesMax = 0;
                    presentGapSumMs = 0;
                    presentGapCount = 0;
                    presentGapMaxMs = 0;
                    preAcquireMs = 0;
                    preAcquireMaxMs = 0;
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
                        duplication!.Dispose();
                        // Null out before the recreate attempt - if it throws, `duplication`
                        // must not be left pointing at the just-disposed object, or the next
                        // AcquireNextFrame call below crashes the whole loop with an NRE
                        // instead of just retrying (see the null-guard above the acquire call).
                        duplication = null;
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
                            lock (_bufferLock) _pauseEvents.Add(new PauseEvent(MonotonicClock.UtcNow, false));
                        }
                    }
                }

                // Whole-iteration wall time (previous AcquireNextFrame call
                // through this one) - compare against
                // waitMs+copyMapMs+scaleMs+encodeMs+getFrameMs in the diag
                // line below: if this runs meaningfully higher than the sum
                // of the named stages, something between them (diag
                // logging, the once-a-second target-window recheck/DXGI
                // recreate, GC, thread scheduling) is eating time none of
                // them are watching.
                var preAcquireElapsedMs = preAcquireStopwatch.Elapsed.TotalMilliseconds;
                preAcquireStopwatch.Restart();
                preAcquireMs += preAcquireElapsedMs;
                if (preAcquireElapsedMs > preAcquireMaxMs) preAcquireMaxMs = preAcquireElapsedMs;
                var gen2CountNow = GC.CollectionCount(2);
                var gen1CountNow = GC.CollectionCount(1);
                if (preAcquireElapsedMs > 200)
                {
                    // If gen1Delta/gen2Delta are both 0, this gap has nothing
                    // to do with GC - back to looking at DXGI/scheduling. If
                    // either incremented, a blocking collection ran somewhere
                    // in this window and froze every managed thread,
                    // including this one - managedMb shows how big a heap
                    // that collection had to walk.
                    AppLog.Info($"Native capture: {preAcquireElapsedMs:0}ms iteration-to-iteration gap - gen1Delta={gen1CountNow - gen1CountAtLastIteration}, gen2Delta={gen2CountNow - gen2CountAtLastIteration}, managedMb={GC.GetTotalMemory(false) / (1024 * 1024)}.");
                }

                gen1CountAtLastIteration = gen1CountNow;
                gen2CountAtLastIteration = gen2CountNow;

                iterationsSinceLog++;
                stageStopwatch.Restart();
                // A long (500ms) timeout meant this call itself blocked well
                // past a single frame interval whenever the source hadn't
                // produced anything new yet, so the pacing-gate/encode step
                // below (which only ran AFTER a successful acquire) could
                // fall arbitrarily far behind wall-clock time during any lull
                // in the source's own present rate - confirmed via
                // avgPresentGapMs/avgAccumulatedFrames staying ~1 (we were
                // never actually behind the source) while encoded fps still
                // measured well under target, meaning frames were being
                // skipped rather than duplicated to fill those gaps, unlike
                // every other capture tool (OBS, ScreenRecorderLib), which
                // pads with the last frame instead. A short timeout keeps
                // this loop returning often enough for the pacing gate below
                // (now unconditional, not gated on a successful acquire) to
                // actually catch up and duplicate-encode on schedule.
                // duplication can be null here if a prior recreate attempt (target
                // switch or access-loss recovery below) failed - retry it every
                // iteration with a short backoff instead of dereferencing null,
                // which previously crashed the whole capture session on any
                // transient DuplicateOutput failure (e.g. a fullscreen-exclusive
                // transition briefly denying access).
                if (duplication is null)
                {
                    Thread.Sleep(50);
                    try
                    {
                        duplication = CreateDuplicationFor(device, targetHandle, out desktopBounds);
                        AppLog.Info("Native capture: DXGI duplication recreated after prior failure.");
                    }
                    catch (Exception error)
                    {
                        AppLog.Error("Native capture: duplication recreate retry failed.", error);
                    }
                    continue;
                }

                var acquireResult = duplication.AcquireNextFrame(AcquireTimeoutMs, out var frameInfo, out var desktopResource);
                waitMs += stageStopwatch.Elapsed.TotalMilliseconds;

                var occluded = !isMonitorMode && !IsWindowForegroundAndVisible(targetHandle);

                if (acquireResult.Success)
                {
                    // LastPresentTime is 0 when the desktop IMAGE itself hasn't
                    // actually changed since the last delivered frame (e.g. only
                    // the OS cursor moved) - AcquireNextFrame still "succeeds" for
                    // these, so without this check every one of them was being
                    // treated as fresh content: cropped, GPU-scaled, and burning a
                    // pacing-gate slot on byte-identical data instead of the next
                    // genuinely new frame.
                    if (frameInfo.LastPresentTime == 0)
                    {
                        zeroPresentSkips++;
                        duplication.ReleaseFrame();
                        desktopResource.Dispose();
                    }
                    else
                    {
                        accumulatedFramesSum += frameInfo.AccumulatedFrames;
                        if (frameInfo.AccumulatedFrames > accumulatedFramesMax) accumulatedFramesMax = frameInfo.AccumulatedFrames;
                        if (lastRealPresentTicks != 0)
                        {
                            var gapMs = (frameInfo.LastPresentTime - lastRealPresentTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                            presentGapSumMs += gapMs;
                            presentGapCount++;
                            if (gapMs > presentGapMaxMs) presentGapMaxMs = gapMs;
                        }
                        lastRealPresentTicks = frameInfo.LastPresentTime;

                        try
                        {
                            framesSeen++;

                            stageStopwatch.Restart();
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

                            if (!occluded && (cropWidth != captureWidth || cropHeight != captureHeight))
                            {
                                captureWidth = Math.Max(2, cropWidth);
                                captureHeight = Math.Max(2, cropHeight);
                                staging.Dispose();
                                staging = CreateStagingTexture(device, captureWidth, captureHeight);
                                ffmpeg.sws_freeContext(swsContext);
                                swsContext = CreateScaler(captureWidth, captureHeight, outputWidth, outputHeight);

                                if (useGpuScale)
                                {
                                    inputView!.Dispose();
                                    croppedTexture!.Dispose();
                                    (croppedTexture, inputView) = CreateGpuCropInputView(device, videoDevice!, vpEnumerator!, captureWidth, captureHeight);
                                }
                            }

                            if (!occluded)
                            {
                                // NVENC's actual encode runs asynchronously - avcodec_send_frame
                                // can return before the encoder has finished reading a PREVIOUS
                                // submission of this same reused AVFrame's buffer. Without this,
                                // overwriting frame->data here for the next real frame could race
                                // the encoder still reading the last one, corrupting whatever it
                                // was mid-read on (observed as the frozen/occluded frame coming
                                // out black instead of the real last frame). Only actually makes
                                // a copy if something else still references the old buffer.
                                ffmpeg.av_frame_make_writable(frame);

                                if (useGpuScale)
                                {
                                    stageStopwatch.Restart();
                                    using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                                    {
                                        var box = new Vortice.Mathematics.Box(cropLeft, cropTop, 0, cropLeft + captureWidth, cropTop + captureHeight, 1);
                                        device.ImmediateContext.CopySubresourceRegion(croppedTexture, 0, 0, 0, 0, desktopTexture, 0, box);
                                    }

                                    var stream = new VideoProcessorStream { Enable = true, InputSurface = inputView };
                                    videoContext!.VideoProcessorBlt(videoProcessor, outputView, 0, 1, new[] { stream });
                                    var currentRingIndex = nv12StagingIndex;
                                    device.ImmediateContext.CopyResource(nv12StagingRing![currentRingIndex], nv12Output);
                                    copyMapMs += stageStopwatch.Elapsed.TotalMilliseconds;

                                    // Reads the OTHER ring slot - the copy issued for it last
                                    // time has had a full iteration's worth of async time to
                                    // actually finish, so this Map() doesn't stall waiting on
                                    // the CopyResource that was JUST issued above. First real
                                    // frame has no previous slot ready yet, so it's skipped
                                    // (frame->data keeps its FillFrameBlack content for that
                                    // one frame - same as any other single-frame freeze).
                                    if (nv12RingPrimed)
                                    {
                                        var previousRingIndex = 1 - currentRingIndex;
                                        stageStopwatch.Restart();
                                        var mapped = device.ImmediateContext.Map(nv12StagingRing[previousRingIndex], 0, MapMode.Read, MapFlags.None);
                                        try
                                        {
                                            CopyNv12PlanesToFrame(mapped, outputWidth, outputHeight, frame);
                                        }
                                        finally
                                        {
                                            device.ImmediateContext.Unmap(nv12StagingRing[previousRingIndex], 0);
                                        }
                                        scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;
                                    }
                                    else
                                    {
                                        nv12RingPrimed = true;
                                    }
                                    nv12StagingIndex = 1 - currentRingIndex;
                                }
                                else
                                {
                                    stageStopwatch.Restart();
                                    using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
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
                            }
                            // else: occluded - frame->data still holds the last successfully
                            // scaled content, re-encoded unchanged below (visual freeze).
                        }
                        finally
                        {
                            duplication.ReleaseFrame();
                            desktopResource.Dispose();
                        }
                    }
                }
                else
                {
                    desktopResource?.Dispose();
                    if (acquireResult.Code == ResultCode.AccessLost.Code)
                    {
                        AppLog.Info("Native capture: DXGI duplication access lost, recreating.");
                        duplication.Dispose();
                        duplication = null;
                        try
                        {
                            duplication = CreateDuplicationFor(device, targetHandle, out desktopBounds);
                        }
                        catch (Exception error)
                        {
                            AppLog.Error("Native capture: failed to recreate DXGI duplication after access loss.", error);
                            Thread.Sleep(200);
                        }
                    }
                    else if (acquireResult.Code != ResultCode.WaitTimeout.Code)
                    {
                        // Transient failure (e.g. desktop switch) - brief backoff, retry.
                        Thread.Sleep(50);
                    }
                    // WaitTimeout: genuinely nothing new from the source yet this
                    // cycle - fall through to the pacing gate below exactly like a
                    // successful-but-occluded frame would, so frame->data's last
                    // real content still gets duplicate-encoded on schedule instead
                    // of the encoded frame rate just falling behind.
                }

                if (occluded != isPaused)
                {
                    isPaused = occluded;
                    lock (_bufferLock) _pauseEvents.Add(new PauseEvent(MonotonicClock.UtcNow, isPaused));
                    AppLog.Info($"Native capture: recording {(isPaused ? "paused (window not foreground)" : "resumed")}.");
                }

                if (!occluded && !hasCapturedRealFrame)
                {
                    hasCapturedRealFrame = true;
                    // lastEncodedAt is still its initial/stale value from however
                    // long the buffer sat waiting for focus - reset it to now so
                    // the catch-up gate below doesn't treat that entire wait as a
                    // pacing gap to fill with duplicate frames.
                    lastEncodedAt = stopwatch.Elapsed;
                    if (fullSessionFormatContext is not null)
                    {
                        // Full Session's muxed audio window is requested starting
                        // at fullSessionStartUtc (see FinalizeFullSessionRecording)
                        // - it was set at buffer-arm time above, before the window
                        // ever had focus. Re-anchor it to this, the actual first
                        // recorded video frame, so audio isn't muxed several
                        // seconds ahead of where the video track now starts.
                        fullSessionStartUtc = MonotonicClock.UtcNow;
                        fullSessionStartWallUtc = DateTime.UtcNow;
                    }
                    AppLog.Info("Native capture: first foreground frame captured, recording started.");
                }

                // Pacing/encode gate - now evaluated every iteration regardless of
                // whether this exact cycle produced fresh content, instead of only
                // running inside the successful-real-frame branch. Previously the
                // encoded frame RATE was capped by however fast the SOURCE happened
                // to deliver genuinely new presents (measured via LastPresentTime:
                // averaging ~45fps with real bursts past 150fps and lulls near
                // 40fps, while avgAccumulatedFrames stayed ~1 the whole time -
                // proof this loop was never actually falling behind the source, it
                // was just refusing to pad for it). Every other capture tool pads
                // with a duplicate of the last frame when nothing new has arrived
                // in time to keep actual encoded fps locked to the target; this now
                // does the same, reusing frame->data unchanged (identical mechanism
                // to the existing occlusion freeze) whenever nothing fresh landed.
                //
                // A `while` here (not `if`) instead of jumping lastEncodedAt to
                // "now" catches up with MULTIPLE duplicate-encoded frames if a
                // real stall (e.g. an AccessLost recreation, a Thread.Sleep(50)
                // backoff) ever eats more than one interval's worth of real time,
                // so the declared/ideal timeline below never silently falls behind
                // real elapsed time.
                //
                // Capped, though - a genuine multi-minute stall (seen under heavy
                // GPU load/driver hiccups) would otherwise make this loop pad
                // through the ENTIRE gap as thousands of duplicate-encoded copies
                // of one frozen frame, ballooning both encoded frame count and the
                // clip's own PTS-derived duration far past what was requested (a
                // "1 minute" replay length saving a 7+ minute, almost entirely
                // static clip). Past a couple of seconds' worth of padding, snap
                // the ideal timeline forward to now instead of mechanically
                // filling every missed slot.
                // Skipped entirely until the target window has been in the
                // foreground at least once - see hasCapturedRealFrame's
                // declaration above for why (avoids ever writing the
                // FillFrameBlack placeholder into the ring buffer/full
                // session as real recorded content).
                if (hasCapturedRealFrame)
                {
                var catchUpFramesRemaining = Math.Clamp(config.FrameRate, 15, 240) * 2;
                while (stopwatch.Elapsed - lastEncodedAt >= targetFrameInterval)
                {
                    if (catchUpFramesRemaining-- <= 0)
                    {
                        AppLog.Info($"Native capture: pacing gap of {(stopwatch.Elapsed - lastEncodedAt).TotalSeconds:0.0}s exceeded catch-up cap - snapping timeline forward instead of padding with duplicate frames.");
                        lastEncodedAt = stopwatch.Elapsed;
                        break;
                    }
                    lastEncodedAt += targetFrameInterval;
                    framesEncoded++;
                    framesEncodedSinceLog++;

                    // An ideal, constant-rate timestamp (frame index * the exact
                    // target interval) instead of real elapsed time - the file's
                    // computed average frame rate (what File Explorer/players
                    // show) is then EXACTLY the configured target by construction,
                    // instead of a close-but-jittery approximation from real
                    // scheduler timing. Audio alignment doesn't use this - it gets
                    // its own real wall-clock timestamp below, specifically so
                    // idealizing video's timeline can't reintroduce the audio-sync
                    // bug that was just fixed.
                    frame->pts = (long)Math.Round(encodedFrameIndex * idealFrameIntervalMicroseconds);
                    encodedFrameIndex++;

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

                    // avcodec_send_frame/receive_packet themselves now run on
                    // EncodeLoop's own thread (see its declaration above) - a slow
                    // NVENC call under real GPU contention used to block THIS
                    // thread, stalling AcquireNextFrame right along with it (the
                    // capture freeze this whole diagnostic trail was chasing:
                    // avgEncodeMs spiking 20x+ with frames backing up hundreds
                    // deep). av_frame_clone is a cheap ref-counted handle, not a
                    // pixel copy - av_frame_make_writable up above already treats
                    // "something else still references this buffer" as
                    // copy-on-write, so the encode thread holding this clone a
                    // while longer just means the NEXT capture-thread frame gets
                    // a fresh buffer instead of racing this one, exactly the
                    // mechanism that comment already relies on.
                    stageStopwatch.Restart();
                    var clonedFrame = ffmpeg.av_frame_clone(frame);
                    if (clonedFrame is null)
                    {
                        AppLog.Error("Native capture: av_frame_clone failed, dropping a frame.");
                        Interlocked.Increment(ref _encodeDroppedCount);
                    }
                    else if (!encodeQueue.TryAdd(new EncodeJob((nint)clonedFrame, MonotonicClock.UtcNow)))
                    {
                        // Queue's genuinely full - the encoder can't keep pace even
                        // decoupled, not just a transient stall. Drop rather than
                        // block (defeats the whole point) or grow unbounded.
                        var droppedFrame = clonedFrame;
                        ffmpeg.av_frame_free(&droppedFrame);
                        Interlocked.Increment(ref _encodeDroppedCount);
                    }
                    encodeMs += stageStopwatch.Elapsed.TotalMilliseconds;
                }
                }

                if (stopwatch.Elapsed - lastRingTrim >= TimeSpan.FromSeconds(1))
                {
                    lastRingTrim = stopwatch.Elapsed;
                    TrimRingBuffer(fullSessionFormatContext is not null ? fullSessionStartUtc : (DateTime?)null);
                    // Audio captures ended mid-session (e.g. a route change when the
                    // game/chat app/mic changes - see AudioCapturePipeline.
                    // StopStaleAudioCaptures) only get their file handle closed, not
                    // deleted; without this the raw WAV files pile up on disk for the
                    // entire lifetime of a long-running session instead of being
                    // cleaned up as soon as they're no longer needed. While a full
                    // session is recording, never prune past its start - its finalize
                    // muxes audio from session start, including captures that ended
                    // mid-session (4GiB WAV rollovers, route changes).
                    var audioCutoffUtc = MonotonicClock.UtcNow - Duration - TimeSpan.FromSeconds(5);
                    if (fullSessionFormatContext is not null && fullSessionStartUtc < audioCutoffUtc) audioCutoffUtc = fullSessionStartUtc;
                    _audio.PruneOlderThan(audioCutoffUtc);
                }
            }

            // Stop accepting new jobs and wait for EncodeLoop to drain everything
            // already queued (including its own final flush of whatever's still
            // buffered inside the encoder) - the finally block below also does
            // this on any exception path, so this is a no-op there, not a
            // duplicate drain.
            encodeQueue.CompleteAdding();
            encodeThread.Join();
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
            // Guarantee the encode thread is fully stopped (and has released its
            // last cloned frame) before codecContext/packet get freed below, on
            // EVERY exit path - the happy path above already did this, so this is
            // a no-op there; an exception thrown mid-loop is the path that
            // actually needs it here.
            try
            {
                encodeQueue?.CompleteAdding();
                encodeThread?.Join();
            }
            catch (Exception error)
            {
                AppLog.Error("Native capture: encode thread shutdown failed.", error);
            }
            encodeQueue?.Dispose();

            if (frame is not null) { var f = frame; ffmpeg.av_frame_free(&f); }
            if (packet is not null) { var p = packet; ffmpeg.av_packet_free(&p); }
            if (swsContext is not null) ffmpeg.sws_freeContext(swsContext);
            FinalizeFullSessionWriter(fullSessionFormatContext);
            if (!string.IsNullOrEmpty(fullSessionTempVideoPath))
            {
                var finalizeConfig = _configProvider();
                if (finalizeConfig.FullSessionBackgroundFinalize)
                {
                    // Snapshot the capture set NOW - StopAsync clears the live
                    // list (without deleting files, see its comment) the
                    // moment this loop returns.
                    var captureSnapshot = _audio.SnapshotCaptures();
                    var startUtc = fullSessionStartUtc;
                    var startWallUtc = fullSessionStartWallUtc;
                    var tempPath = fullSessionTempVideoPath;
                    var finalPath = fullSessionFinalOutputPath;
                    var gameName = fullSessionGameDisplayName;

                    // Make the session visible IMMEDIATELY as a video-only
                    // file; the background job then muxes audio into it via a
                    // swap. If the move fails (cross-volume oddity), the job
                    // just works from the temp file like the synchronous path.
                    var videoPath = tempPath;
                    try
                    {
                        File.Move(tempPath, finalPath);
                        videoPath = finalPath;
                        var immediateGameName = !string.IsNullOrWhiteSpace(gameName) && !string.Equals(gameName, "No game detected", StringComparison.OrdinalIgnoreCase)
                            ? gameName
                            : finalizeConfig.GameDisplayName;
                        ClipInfoSidecar.Save(finalizeConfig.LibraryFolder, finalPath, new ClipInfo(immediateGameName, null, $"Session - {immediateGameName}", startWallUtc));
                        AppLog.Info($"Full session video available immediately (audio attaching in background): {finalPath}.");
                    }
                    catch (Exception error)
                    {
                        AppLog.Error("Full session immediate video move failed; background finalize will produce the file instead.", error);
                    }

                    Interlocked.Increment(ref _activeBackgroundFinalizes);
                    var capturedVideoPath = videoPath;
                    _backgroundFinalize = Task.Run(() =>
                    {
                        try
                        {
                            FinalizeFullSessionRecording(finalizeConfig, startUtc, startWallUtc, capturedVideoPath, finalPath, gameName, captureSnapshot);
                        }
                        finally
                        {
                            foreach (var path in captureSnapshot.FilePaths) AudioCapturePipeline.TryDelete(path);
                            Interlocked.Decrement(ref _activeBackgroundFinalizes);
                            AppLog.Info($"Full session background finalize complete: final={finalPath}.");
                        }
                    });
                }
                else
                {
                    FinalizeFullSessionRecording(finalizeConfig, fullSessionStartUtc, fullSessionStartWallUtc, fullSessionTempVideoPath, fullSessionFinalOutputPath, fullSessionGameDisplayName);
                }
            }
            if (codecContext is not null) { var c = codecContext; ffmpeg.avcodec_free_context(&c); }
            duplication?.Dispose();
            staging?.Dispose();
            inputView?.Dispose();
            croppedTexture?.Dispose();
            outputView?.Dispose();
            if (nv12StagingRing is not null) foreach (var t in nv12StagingRing) t.Dispose();
            nv12Output?.Dispose();
            videoProcessor?.Dispose();
            vpEnumerator?.Dispose();
            videoContext?.Dispose();
            videoDevice?.Dispose();
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

    // Sets up the D3D11 Video Processor to do the crop->NV12-downscale that
    // sws_scale otherwise does on the CPU, entirely on the GPU. Only the
    // final small output-resolution NV12 texture ever gets read back to the
    // CPU afterward (via nv12Staging), instead of the full captured crop
    // (often 4K) every single frame. Throws on any failure - caller treats
    // that as "not supported on this hardware/driver" and falls back to CPU
    // scale, so nothing here needs to be defensive beyond that.
    private static (ID3D11VideoDevice VideoDevice, ID3D11VideoContext VideoContext, ID3D11VideoProcessorEnumerator Enumerator, ID3D11VideoProcessor Processor, ID3D11Texture2D Nv12Output, ID3D11Texture2D[] Nv12StagingRing, ID3D11VideoProcessorOutputView OutputView)
        CreateGpuScaler(ID3D11Device device, int captureWidth, int captureHeight, int outputWidth, int outputHeight, int frameRate)
    {
        var videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        var videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        var contentDescription = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)captureWidth,
            InputHeight = (uint)captureHeight,
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            InputFrameRate = new Rational((uint)Math.Clamp(frameRate, 15, 240), 1),
            OutputFrameRate = new Rational((uint)Math.Clamp(frameRate, 15, 240), 1),
            Usage = VideoUsage.PlaybackNormal
        };
        ID3D11VideoProcessorEnumerator enumerator;
        try
        {
            enumerator = videoDevice.CreateVideoProcessorEnumerator(contentDescription);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"CreateVideoProcessorEnumerator failed: {error.Message}", error);
        }

        ID3D11VideoProcessor processor;
        try
        {
            processor = videoDevice.CreateVideoProcessor(enumerator, 0);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"CreateVideoProcessor failed: {error.Message}", error);
        }

        // Many D3D11 video processing samples create the VP output resource
        // with BindFlags.RenderTarget even though nothing ever binds it as
        // one - some drivers reject CreateVideoProcessorOutputView with
        // E_INVALIDARG on a plain BindFlags.None Default texture otherwise.
        ID3D11Texture2D nv12Output;
        try
        {
            nv12Output = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outputWidth,
                Height = (uint)outputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
                BindFlags = BindFlags.RenderTarget
            });
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"CreateTexture2D (nv12Output) failed: {error.Message}", error);
        }

        ID3D11VideoProcessorOutputView outputView;
        try
        {
            outputView = videoDevice.CreateVideoProcessorOutputView(nv12Output, enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D
            });
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"CreateVideoProcessorOutputView failed: {error.Message}", error);
        }

        var nv12StagingRing = new[]
        {
            CreateNv12StagingTexture(device, outputWidth, outputHeight),
            CreateNv12StagingTexture(device, outputWidth, outputHeight)
        };

        return (videoDevice, videoContext, enumerator, processor, nv12Output, nv12StagingRing, outputView);
    }

    private static ID3D11Texture2D CreateNv12StagingTexture(ID3D11Device device, int width, int height)
    {
        return device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        });
    }

    // The crop-sized GPU-only source texture the Video Processor reads from,
    // and its input view - rebuilt whenever the crop size changes (window
    // resize), same as the CPU path's staging texture/swsContext.
    private static (ID3D11Texture2D CroppedTexture, ID3D11VideoProcessorInputView InputView) CreateGpuCropInputView(
        ID3D11Device device, ID3D11VideoDevice videoDevice, ID3D11VideoProcessorEnumerator enumerator, int width, int height)
    {
        var croppedTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            BindFlags = BindFlags.None
        });

        var inputView = videoDevice.CreateVideoProcessorInputView(croppedTexture, enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
        });

        return (croppedTexture, inputView);
    }

    // D3D11's NV12 textures map as a single contiguous surface: the luma (Y)
    // plane's rows first, then the chroma (interleaved UV) plane's rows
    // immediately after, both using the SAME row pitch - not two separate
    // Map calls. Copied per-row since the D3D11 stride (mapped.RowPitch) and
    // ffmpeg's own allocated stride (frame->linesize) are aligned
    // differently and can't just be bulk-memcpy'd as one block.
    private static unsafe void CopyNv12PlanesToFrame(MappedSubresource mapped, int width, int height, AVFrame* frame)
    {
        var srcStride = (int)mapped.RowPitch;
        var ySrc = (byte*)mapped.DataPointer;
        var yDst = frame->data[0];
        var yDstStride = frame->linesize[0];
        for (var row = 0; row < height; row++)
        {
            Buffer.MemoryCopy(ySrc + row * srcStride, yDst + row * yDstStride, yDstStride, width);
        }

        var uvHeight = (height + 1) / 2;
        var uvSrc = ySrc + srcStride * height;
        var uvDst = frame->data[1];
        var uvDstStride = frame->linesize[1];
        for (var row = 0; row < uvHeight; row++)
        {
            Buffer.MemoryCopy(uvSrc + row * srcStride, uvDst + row * uvDstStride, uvDstStride, width);
        }
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

    // FramePtr is an AVFrame* smuggled across the thread boundary as nint -
    // pointer types can't be used as generic type arguments (BlockingCollection<T>
    // here) or captured by a lambda closure, both of which this needs. Owned by
    // whichever side currently holds it: the capture thread until TryAdd
    // succeeds, EncodeLoop from then on (which is responsible for freeing it).
    private readonly record struct EncodeJob(nint FramePtr, DateTime WallClockUtc);

    // Runs avcodec_send_frame/receive_packet (and so DrainToRingBuffer, and the
    // full-session mux write inside it) on its own thread, decoupled from
    // CaptureLoop's AcquireNextFrame loop. Existed as a single synchronous call
    // inline in CaptureLoop originally - fine when NVENC keeps up, but a real
    // GPU-contention stall there (confirmed via avgEncodeMs spiking 20x+ baseline
    // with frames backing up hundreds deep in Native capture diag) blocked
    // AcquireNextFrame right along with it, since it was the same thread. This
    // loop owns codecContext/packet/pendingFrameWallClocks exclusively from here
    // on - CaptureLoop never touches them again after starting this thread.
    private unsafe void EncodeLoop(BlockingCollection<EncodeJob> queue, nint codecContextPtr, nint packetPtr, nint fullSessionFormatContextPtr, nint fullSessionStreamPtr)
    {
        var codecContext = (AVCodecContext*)codecContextPtr;
        var packet = (AVPacket*)packetPtr;
        var fullSessionFormatContext = (AVFormatContext*)fullSessionFormatContextPtr;
        var fullSessionStream = (AVStream*)fullSessionStreamPtr;
        // Same FIFO purpose as the original inline version - see DrainToRingBuffer's
        // dequeue site - just living here now since send_frame moved here with it.
        var pendingFrameWallClocks = new Queue<DateTime>();
        try
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                var jobFrame = (AVFrame*)job.FramePtr;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    pendingFrameWallClocks.Enqueue(job.WallClockUtc);
                    if (ffmpeg.avcodec_send_frame(codecContext, jobFrame) == 0)
                    {
                        DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream, pendingFrameWallClocks);
                    }
                }
                finally
                {
                    ffmpeg.av_frame_free(&jobFrame);
                }
                Interlocked.Add(ref _encodeMicrosAccum, (long)(sw.Elapsed.TotalMilliseconds * 1000));
                Interlocked.Increment(ref _encodeCountAccum);
            }

            // Queue drained and CompleteAdding was called (CaptureLoop's while
            // loop exited) - flush whatever's still buffered inside the encoder
            // itself, same as the original inline flush used to.
            ffmpeg.avcodec_send_frame(codecContext, null);
            DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream, pendingFrameWallClocks);
        }
        catch (Exception error)
        {
            // Must not throw unhandled off this thread - an unobserved exception
            // on a plain Thread (unlike Task) crashes the whole process.
            AppLog.Error("Native capture: encode thread failed.", error);
        }
    }

    private unsafe void DrainToRingBuffer(AVCodecContext* codecContext, AVPacket* packet, AVFormatContext* fullSessionFormatContext, AVStream* fullSessionStream, Queue<DateTime> pendingFrameWallClocks)
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

            // Dequeues the real timestamp of whichever frame THIS packet
            // actually corresponds to (FIFO order, matching the encoder's own
            // in-order output guarantee with max_b_frames=0) - not just "now",
            // since the encoder can hold frames internally for a call or two
            // before releasing output, and packet->pts is now an IDEAL,
            // constant-rate timestamp (see the pacing gate in CaptureLoop) so
            // it can't be used to derive this the way it used to be.
            var realWallClockUtc = pendingFrameWallClocks.Count > 0 ? pendingFrameWallClocks.Dequeue() : MonotonicClock.UtcNow;

            lock (_bufferLock)
            {
                _packets.Add(new RingPacket(data, packet->pts, isKeyframe, realWallClockUtc));
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
            finalOutputPath = ClipFileNaming.BuildUniquePath(config.FullSessionRecordingFolder, ClipFileNaming.BuildFileName(sessionLabel, DateTime.Now, "mp4", config.ClipFileNameScheme, config.CustomClipFileNameTemplate, config.GameDisplayName));
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
    // sessionStartUtc is on the MonotonicClock timeline (audio/pause alignment);
    // sessionStartWallUtc is the real wall-clock start, used only for the
    // sidecar's user-facing CreatedAt.
    private void FinalizeFullSessionRecording(ReplayBufferConfig config, DateTime sessionStartUtc, DateTime sessionStartWallUtc, string tempVideoPath, string finalOutputPath, string sessionGameDisplayName = "", AudioCapturePipeline.CaptureSetSnapshot? capturesOverride = null)
    {
        if (string.IsNullOrEmpty(tempVideoPath) || string.IsNullOrEmpty(finalOutputPath)) return;

        // The game the session was RECORDED from, not whatever detection says
        // at finalize time - the session usually ends precisely because the
        // game closed, so the fresh config here reads "No game detected" and
        // that's what the library tile showed. Start-time identity wins;
        // finalize-time only fills in if the session began before any game
        // was detected.
        var gameDisplayName = !string.IsNullOrWhiteSpace(sessionGameDisplayName) && !string.Equals(sessionGameDisplayName, "No game detected", StringComparison.OrdinalIgnoreCase)
            ? sessionGameDisplayName
            : config.GameDisplayName;

        var snapshots = new List<string>();
        try
        {
            var sessionEndUtc = MonotonicClock.UtcNow;
            var durationSeconds = Math.Max(1, (sessionEndUtc - sessionStartUtc).TotalSeconds);
            WritePausedRangesSidecar(config.LibraryFolder, finalOutputPath, ComputePausedRangesSeconds(GetOrderedPauseEvents(), sessionStartUtc, sessionEndUtc));
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
            // See SaveReplayAsync's identical comment - shifts only the audio
            // source window, video's own timeline is untouched.
            var chunkStartUtc = sessionStartUtc - TimeSpan.FromMilliseconds(config.AudioSyncOffsetMs);
            var remainingSeconds = durationSeconds;
            while (remainingSeconds > 0)
            {
                var chunkSeconds = Math.Min(SegmentChunkSeconds, remainingSeconds);
                segmentWindows.Add((chunkStartUtc, chunkSeconds));
                chunkStartUtc += TimeSpan.FromSeconds(chunkSeconds);
                remainingSeconds -= chunkSeconds;
            }

            var tracks = _audio
                .BuildAlignedTracksAsync(segmentWindows, config, snapshots, CancellationToken.None, capturesOverride)
                .GetAwaiter().GetResult();

            // Background finalize already moved the video-only file onto the
            // final path so the session is visible immediately - ffmpeg can't
            // write its own input, so mux to a sibling temp and swap after.
            var muxInPlace = string.Equals(tempVideoPath, finalOutputPath, StringComparison.OrdinalIgnoreCase);
            var muxOutputPath = muxInPlace ? finalOutputPath + ".muxing.mp4" : finalOutputPath;

            List<string> BuildMuxArgs(string[] videoCodecArgs)
            {
                var muxArgs = new List<string> { "-y", "-i", tempVideoPath };
                foreach (var track in tracks) muxArgs.AddRange(new[] { "-i", track.Path });
                muxArgs.AddRange(new[] { "-map", "0:v" });
                for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { "-map", $"{i + 1}:a" });
                muxArgs.AddRange(videoCodecArgs);
                muxArgs.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { $"-metadata:s:a:{i}", $"title={tracks[i].Label}" });
                // +faststart moves the moov index to the front of the file.
                // Costs one extra file rewrite at finalize, but without it
                // every later reader (LibVLC, ffmpeg chunk/waveform/thumbnail
                // extraction) must first seek to the END of a multi-GB file to
                // find the index - painless locally, a seek storm over a
                // network drive that made long sessions stutter/fail in the
                // editor while plain VLC (single reader, patient) coped.
                muxArgs.AddRange(new[] { "-movflags", "+faststart" });
                muxArgs.AddRange(new[] { "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("EVE Native Full Session")}", muxOutputPath });
                return muxArgs;
            }

            // The ring encoder already produced H.264, so H.264 here is a
            // plain stream copy (fast). H.265/AV1 re-encode the whole session
            // through NVENC at finalize time for a much smaller file, falling
            // back to a stream copy (not a CPU encode - a multi-hour software
            // re-encode at finalize would be far worse than a bigger file) if
            // NVENC isn't available.
            var codecArgs = config.FullSessionVideoCodec switch
            {
                "H.265" => new[] { "-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "24", "-b:v", "0" },
                "AV1" => new[] { "-c:v", "av1_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "32", "-b:v", "0" },
                _ => new[] { "-c:v", "copy" }
            };
            var result = AudioCapturePipeline.RunProcessAsync("ffmpeg", BuildMuxArgs(codecArgs), CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode != 0 && codecArgs[1] != "copy")
            {
                AppLog.Error($"Full session {config.FullSessionVideoCodec} re-encode failed, retrying as stream copy: {result.Error}");
                result = AudioCapturePipeline.RunProcessAsync("ffmpeg", BuildMuxArgs(new[] { "-c:v", "copy" }), CancellationToken.None).GetAwaiter().GetResult();
            }
            if (result.ExitCode != 0)
            {
                AppLog.Error($"Full session recording final mux failed: {result.Error}{(muxInPlace ? " (video-only session file kept)" : string.Empty)}");
                if (muxInPlace) AudioCapturePipeline.TryDelete(muxOutputPath);
            }
            else
            {
                if (muxInPlace)
                {
                    File.Move(muxOutputPath, finalOutputPath, overwrite: true);
                }
                ClipInfoSidecar.Save(config.LibraryFolder, finalOutputPath, new ClipInfo(gameDisplayName, null, $"Session - {gameDisplayName}", sessionStartWallUtc));
                AppLog.Info($"Native full session recording saved: path={finalOutputPath}, codec={config.FullSessionVideoCodec}.");
                EnforceFullSessionQuota(config);
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Full session recording finalize/mux failed", error);
        }
        finally
        {
            // In-place mode the "temp" IS the final file - never delete it.
            if (!string.Equals(tempVideoPath, finalOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                AudioCapturePipeline.TryDelete(tempVideoPath);
            }
            foreach (var snapshot in snapshots) AudioCapturePipeline.TryDelete(snapshot);
        }
    }

    // Deletes the oldest EVE-recorded session files (identified by their own
    // sidecar's "... Full Session" FileTitle - never touches clips or files
    // EVE didn't write) until the library's VODs tree fits the configured
    // quota again. Runs after each successful session save; the just-saved
    // file is always kept even if it alone exceeds the quota.
    private static void EnforceFullSessionQuota(ReplayBufferConfig config)
    {
        if (config.FullSessionQuotaGb <= 0 || string.IsNullOrWhiteSpace(config.LibraryFolder)) return;
        try
        {
            var vodsRoot = LibraryLayout.VodsRoot(config.LibraryFolder);
            if (!Directory.Exists(vodsRoot)) return;

            var sessions = Directory.EnumerateFiles(vodsRoot, "*.*", SearchOption.AllDirectories)
                .Where(MediaProbeService.IsVideoFile)
                .Where(path =>
                {
                    // New sessions title as "Session - {game}"; pre-existing
                    // ones as "{game} Full Session" - quota must keep seeing both.
                    var title = ClipInfoSidecar.Load(config.LibraryFolder, path)?.FileTitle;
                    return title is not null &&
                           (title.StartsWith("Session - ", StringComparison.OrdinalIgnoreCase) ||
                            title.EndsWith("Full Session", StringComparison.OrdinalIgnoreCase));
                })
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.CreationTimeUtc)
                .ToList();

            var quotaBytes = (long)config.FullSessionQuotaGb * 1024 * 1024 * 1024;
            var totalBytes = sessions.Sum(info => info.Length);
            // Index-bounded to Count-1 so the newest session always survives.
            for (var i = 0; totalBytes > quotaBytes && i < sessions.Count - 1; i++)
            {
                var victim = sessions[i];
                try
                {
                    File.Delete(victim.FullName);
                    ClipInfoSidecar.Delete(config.LibraryFolder, victim.FullName);
                    ClipEditSidecar.Delete(config.LibraryFolder, victim.FullName);
                    AudioCapturePipeline.TryDelete(LibraryLayout.SidecarPath(config.LibraryFolder, victim.FullName, ".paused.json"));
                    totalBytes -= victim.Length;
                    AppLog.Info($"Full session quota: deleted oldest session {victim.Name} ({victim.Length / (1024.0 * 1024 * 1024):0.0}GB) to fit {config.FullSessionQuotaGb}GB.");
                }
                catch (Exception error)
                {
                    AppLog.Error($"Full session quota: failed deleting {victim.FullName}", error);
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Full session quota enforcement failed", error);
        }
    }

    private void TrimRingBuffer(DateTime? fullSessionStartUtc)
    {
        var cutoff = MonotonicClock.UtcNow - Duration - TimeSpan.FromSeconds(5);
        lock (_bufferLock)
        {
            var removeCount = 0;
            while (removeCount < _packets.Count && _packets[removeCount].WallClockUtc < cutoff) removeCount++;
            if (removeCount > 0) _packets.RemoveRange(0, removeCount);

            // _pauseEvents is shared between ring-buffer clip saves (which only
            // ever need the last Duration worth of history) and a running Full
            // Session recording, which can span hours - trimming to the same
            // Duration-based cutoff used for _packets silently dropped any pause
            // event older than that, so a session-start alt-tab was gone from
            // the sidecar by the time a multi-hour session finished. While a
            // Full Session is active, nothing older than its own start is
            // eligible for trimming.
            var pauseEventCutoff = fullSessionStartUtc is { } sessionStart && sessionStart < cutoff
                ? sessionStart
                : cutoff;

            // Keeps at most one event before the cutoff (needed so
            // ComputePausedRangesSeconds can still tell what state a save
            // window started in) and drops everything older than that.
            var keepFromIndex = 0;
            for (var i = _pauseEvents.Count - 1; i >= 0; i--)
            {
                if (_pauseEvents[i].WallClockUtc < pauseEventCutoff) { keepFromIndex = i; break; }
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

    private static void WritePausedRangesSidecar(string libraryRoot, string outputPath, List<(double StartSeconds, double EndSeconds)> ranges)
    {
        if (ranges.Count == 0) return;
        try
        {
            var payload = ranges.Select(r => new { start = Math.Round(r.StartSeconds, 2), end = Math.Round(r.EndSeconds, 2) }).ToArray();
            var sidecarPath = LibraryLayout.SidecarPath(libraryRoot, outputPath, ".paused.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(payload));
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

    // Tries hardware encoders in order, falling back to the next if the named encoder
    // either isn't present in this ffmpeg build or fails to open (no matching GPU/driver
    // present - e.g. h264_nvenc exists in the binary on any machine, but avcodec_open2
    // only succeeds if an actual NVIDIA GPU/driver answers it). h264_amf is AMD's
    // equivalent via the AMF SDK; libx264 is the last-resort CPU fallback so capture
    // still works even with no usable hardware encoder at all.
    private static readonly string[] EncoderCandidates = { "h264_nvenc", "h264_amf", "libx264" };

    // h264_nvenc's default preset does real per-frame rate-distortion search,
    // which measured a sustained ~59-60ms/frame (vs. ~0.5ms on p1) during
    // actual Dead by Daylight matches specifically, where the same GPU is also
    // under its heaviest rendering load of the whole session - that 100x
    // per-frame cost, back when encode still ran inline on CaptureLoop's own
    // thread, turned "GPU is busy" into sustained near-1fps capture for
    // minutes at a time. p1 was the fix at the time, at the cost of visible
    // motion-compression artifacts under fast camera movement (looks like
    // dropped frames even though every frame is present and correctly timed -
    // confirmed via ffprobe on an actual saved clip: exact expected frame
    // count, zero duplicates, dead-even PTS spacing). Now that encode runs on
    // its own thread (see EncodeLoop) decoupled from AcquireNextFrame, a
    // slower preset can no longer reproduce that stall - worst case is
    // EncodeLoop's queue backing up (watch queueDepth/droppedFrames in Native
    // capture diag), not a frozen capture thread. p4 trades some of that
    // encode headroom back for quality. Applied to priv_data before
    // avcodec_open2 - these are encoder-specific options, not real
    // AVCodecContext fields, so they have to land before open, not after.
    // Best-effort: an unsupported option name just logs and moves on instead
    // of failing the whole encoder open, since exact option support varies by
    // ffmpeg build/driver version.
    private static unsafe void ApplyLowLatencyEncoderOptions(AVCodecContext* codecContext, string candidateName)
    {
        void TrySet(string name, string value)
        {
            var result = ffmpeg.av_opt_set(codecContext->priv_data, name, value, 0);
            if (result < 0)
            {
                AppLog.Info($"Native encoder probe: {candidateName} option {name}={value} not supported (error {result}), skipping.");
            }
        }

        switch (candidateName)
        {
            case "h264_nvenc":
                TrySet("preset", "p4");
                TrySet("tune", "ll");
                break;
            case "h264_amf":
                TrySet("usage", "ultralowlatency");
                TrySet("quality", "speed");
                break;
            case "libx264":
                TrySet("preset", "ultrafast");
                TrySet("tune", "zerolatency");
                break;
        }
    }

    private static unsafe AVCodecContext* CreateEncoder(ReplayBufferConfig config, int width, int height, out AVRational timeBase, out string encoderName)
    {
        timeBase = new AVRational { num = 1, den = 1_000_000 };
        encoderName = string.Empty;

        foreach (var candidateName in EncoderCandidates)
        {
            var candidateCodec = ffmpeg.avcodec_find_encoder_by_name(candidateName);
            if (candidateCodec is null)
            {
                AppLog.Info($"Native encoder probe: {candidateName} not present in this ffmpeg build, skipping.");
                continue;
            }

            var codecContext = ffmpeg.avcodec_alloc_context3(candidateCodec);
            codecContext->width = width;
            codecContext->height = height;
            codecContext->time_base = timeBase;
            codecContext->framerate = new AVRational { num = Math.Clamp(config.FrameRate, 15, 240), den = 1 };
            codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            codecContext->bit_rate = CaptureBitrate(config);
            codecContext->gop_size = 240;
            codecContext->max_b_frames = 0;
            codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            ApplyLowLatencyEncoderOptions(codecContext, candidateName);

            var openResult = ffmpeg.avcodec_open2(codecContext, candidateCodec, null);
            if (openResult == 0)
            {
                encoderName = candidateName;
                AppLog.Info($"Native encoder probe: {candidateName} opened successfully.");
                return codecContext;
            }

            AppLog.Info($"Native encoder probe: {candidateName} failed to open (error {openResult}) - no matching GPU/driver, trying next.");
            var failedContext = codecContext;
            ffmpeg.avcodec_free_context(&failedContext);
        }

        throw new InvalidOperationException("No usable H.264 encoder found (tried NVENC, AMD AMF, software libx264).");
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
