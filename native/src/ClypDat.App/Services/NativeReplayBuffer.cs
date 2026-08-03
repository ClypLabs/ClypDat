using ClypDat.Capture.Abstractions;
using FFmpeg.AutoGen;
using System.Buffers;
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

namespace ClypDat.App.Services;

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
// Desktop Duplication is same lower-level API used by this app's Legacy/
// ScreenRecorderLib backend, proven to hit full target fps on
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
// Same DWM dependency also explains low/laggy capture when the GAME has
// vsync off: AcquireNextFrame only gets a new frame on DWM's own present/
// composition cadence, and an uncapped/tearing game presents outside that
// cadence, so DWM skips composing most of its frames and duplication starves
// - confirmed via avgPresentGapMs in the diag log spiking to hundreds of ms
// (vs. ~13ms at a clean 60fps) during exactly this symptom. Turning the
// game's vsync on locks its presents to refresh, so DWM composes every one
// and duplication gets them reliably again. Nothing to fix here - not
// something Desktop Duplication can be worked around from application code.
//
// Falls back to capturing the primary monitor (no crop, no occlusion pausing)
// when no game window is detected. NVENC only for now (no software fallback -
// machines without NVENC should stay on Legacy). Audio reuses
// AudioCapturePipeline - the same Game/Chat/Microphone routing, WASAPI capture, and mux
// logic WindowsReplayBuffer uses, via its own independent instance.
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class NativeReplayBuffer : IReplayBuffer, IReplayCaptureDiagnostics, IAdaptiveCaptureFrameRate
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly string _bufferFolder;
    private readonly AudioCapturePipeline _audio;
    private readonly object _bufferLock = new();
    private readonly List<RingPacket> _packets = new();
    // Running total of _packets' payload bytes, maintained on add/trim under
    // _bufferLock. The diagnostic that reports this used to sum the whole list
    // every 2 seconds - a LINQ walk over up to 14000 entries while holding the
    // one lock the encode thread needs for every packet it produces.
    private long _ringBufferBytes;
    // Live target frame rate. The capture loop owns the pacing interval and
    // reads this once per iteration; anything outside the loop asks via
    // RequestFrameRate rather than touching the interval directly.
    private int _requestedFrameRate;
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
    // Ticks of the last moment genuinely new captured content was scaled into
    // the encoder's frame - the ring buffer alone can't answer "was this clip
    // real?", because a stalled source still produces a full ring of packets,
    // all of them the same padded frame. Written from CaptureLoop, read from
    // SaveReplayAsync on a different thread, so it goes through Volatile.
    private long _lastRealContentTicks;
    private volatile bool _lastSaveVideoWasFrozen;

    public bool LastSaveVideoWasFrozen => _lastSaveVideoWasFrozen;
    // Encode-thread diagnostics (see EncodeLoop) - written with Interlocked from
    // that thread, read/reset from CaptureLoop's own periodic diag line. Plain
    // instance fields are safe here since only one capture session (and so only
    // one encode thread) is ever active at a time.
    private long _encodeMicrosAccum;
    private long _encodeCountAccum;
    private long _encodeDroppedCount;
    private long _totalDroppedFrames;
    private int _peakQueueDepth;
    private DateTime? _lastDegradedUtc;
    private ReplayCaptureHealth _health = ReplayCaptureHealth.Unknown("Native");

    public NativeReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
        _bufferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClypDat",
            "native-replay-buffer");
        _audio = new AudioCapturePipeline(_bufferFolder);
    }

    public bool IsRecording => _sessionActive;

    // Retargets pacing on a running session. Cheap and safe mid-session: only
    // how often a frame is scheduled changes, so the encoder, the GPU scaler
    // and the stream's parameters all stay exactly as they were and packets
    // recorded either side of the change still mux into a single clip.
    //
    // Resolution deliberately has no equivalent. Changing it means rebuilding
    // the encoder, which would leave the ring holding packets of two different
    // sizes - unmuxable into one file - so the only honest implementation would
    // discard the user's buffered replay history at the exact moment the
    // machine is struggling. Frame rate is the lever that costs nothing.
    public void RequestFrameRate(int frameRate)
    {
        if (!_sessionActive) return;
        Volatile.Write(ref _requestedFrameRate, Math.Clamp(frameRate, 15, 240));
    }

    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);
    public event EventHandler? RecordingStopped;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;

    public ReplayCaptureHealth GetHealthSnapshot() => _health;

    private void SetHealth(ReplayCaptureHealth health)
    {
        _health = health;
        HealthChanged?.Invoke(this, health);
    }

    private static void UpdatePeak(ref int value, int candidate)
    {
        var current = Volatile.Read(ref value);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref value, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }

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
        Interlocked.Exchange(ref _totalDroppedFrames, 0);
        Volatile.Write(ref _peakQueueDepth, 0);
        _lastDegradedUtc = null;
        lock (_bufferLock) _pauseEvents.Clear();

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _captureCts = new CancellationTokenSource();
        var token = _captureCts.Token;

        // Set BEFORE starting the capture loop, not after. CaptureLoop runs on
        // its own thread and can fail almost immediately (e.g. the encoder
        // isn't available at all), and its catch sets _sessionActive = false.
        // Assigning true after StartNew raced that: _audio.Start below takes
        // hundreds of ms (device enumeration, per-process loopback init), so a
        // fast failure set false first and then this overwrote it back to true.
        // The buffer then reported IsRecording forever with an empty ring, and
        // every clip attempt surfaced the ring's "Replay just started. Try
        // again in a second." instead of the actual start failure.
        _sessionActive = true;
        SetHealth(new ReplayCaptureHealth("Native", "Desktop Duplication", ReplayCaptureState.Starting,
            config.FrameRate, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty, DateTime.UtcNow));
        _captureTask = Task.Factory.StartNew(
            () => CaptureLoop(token, ready),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            _audio.Start(config);
        }
        catch
        {
            _sessionActive = false;
            throw;
        }

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
            ReturnPooledPackets(0, _packets.Count);
            _packets.Clear();
            _ringBufferBytes = 0;
            _pauseEvents.Clear();
        }
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null)
    {
        if (!_sessionActive) throw new InvalidOperationException("Replay buffer is not recording.");

        var requestedStartUtc = clipWindow?.StartUtc ?? MonotonicClock.UtcNow - Duration;
        var requestedEndUtc = clipWindow?.EndUtc ?? MonotonicClock.UtcNow;
        if (requestedEndUtc <= requestedStartUtc)
        {
            throw new InvalidOperationException("The requested replay window is empty.");
        }

        // Selects and copies in one locked pass - see CopyWindowUnderLock for
        // why the copy is what keeps the ring's pooled payloads safe.
        var window = CopyWindowUnderLock(requestedStartUtc, requestedEndUtc);

        var config = _configProvider();
        var clipName = string.IsNullOrWhiteSpace(titleOverride) ? config.GameDisplayName : titleOverride;
        var gameFolder = Path.Combine(outputFolder, ClipFileNaming.BuildBaseName(config.GameDisplayName));
        Directory.CreateDirectory(gameFolder);
        var outputPath = ClipFileNaming.BuildUniquePath(gameFolder, ClipFileNaming.BuildFileName(clipName, DateTime.Now, "mp4", config.ClipFileNameScheme, config.CustomClipFileNameTemplate, config.GameDisplayName));

        // A capture source can go silent while every stage after it keeps
        // working: the pacing gate pads the last frame, the ring fills with
        // packets, the save succeeds, and the clip is one frozen frame stretched
        // over its full length. The packets can't reveal that - they look
        // completely normal - so compare the window against the last moment new
        // content actually reached the encoder. Not fatal: the audio tracks are
        // still worth keeping, so this only warns.
        var lastRealContentUtc = new DateTime(Volatile.Read(ref _lastRealContentTicks), DateTimeKind.Utc);
        _lastSaveVideoWasFrozen = lastRealContentUtc < window[0].WallClockUtc;
        if (_lastSaveVideoWasFrozen)
        {
            AppLog.Info($"Native replay: saved window contains no new video frames - the clip's video is frozen. Last new frame {(MonotonicClock.UtcNow - lastRealContentUtc).TotalSeconds:0}s ago, path={outputPath}.");
        }

        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"clypdat-native-video-{Guid.NewGuid():N}.mp4");
        var snapshots = new List<string>();
        try
        {
            // Thousands of packet copies and disk writes back to back. Dropping
            // the pooled thread's priority for the duration keeps that burst
            // from competing with the game and the desktop compositor - the
            // body is fully synchronous, so the restore in the finally really
            // does bracket all of the work (the thread returns to the pool
            // afterwards, so it must be put back).
            await Task.Run(() =>
            {
                var previousPriority = Thread.CurrentThread.Priority;
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                try { RemuxWindowToMp4(window, tempVideoPath); }
                finally { Thread.CurrentThread.Priority = previousPriority; }
            }, cancellationToken);

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
            muxArgs.AddRange(new[] { "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("Native")}", outputPath });
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
        // A ring of staging textures rather than one: Map() without DoNotWait
        // blocks until the GPU finishes EVERYTHING queued before it, and
        // mapping the texture we just issued a CopyResource into forces exactly
        // that stall (measured 12-14ms, wildly variable with GPU load -
        // defeating much of the point of GPU scale). Writing one slot while
        // reading an older one whose copy has had time to finish avoids it -
        // standard multi-buffered GPU readback. See the read logic in
        // EncodeScheduledFrame for how a slot is chosen.
        ID3D11Texture2D[]? nv12StagingRing = null;
        var nv12StagingIndex = 0;
        // How many slots hold a copy that was actually issued. Only matters
        // while the ring fills for the first time; after that every slot has
        // been written at least once and this pins at the ring's length.
        var nv12RingWritten = 0;
        ID3D11VideoProcessorOutputView? outputView = null;
        ID3D11VideoProcessorInputView? inputView = null;
        var useGpuScale = false;
        // Set when a present has landed fresh pixels in croppedTexture that
        // have not been converted into frame->data yet. The crop copy runs per
        // present (it has to - the duplication frame is released immediately
        // after), but the conversion it feeds only runs per ENCODED frame, so
        // this is what tells the encode tick whether there is anything new to
        // convert or whether it should just re-encode the last content as a
        // duplicate. See the encode-time conversion in EncodeScheduledFrame.
        var croppedDirty = false;
        // Cursor position for the GPU path, already converted into OUTPUT-resolution
        // pixels at crop time. The crop block is the only place the crop origin and
        // capture size are in scope, but the cursor has to be drawn after the scaled
        // readback (drawing it before would mean the video processor resamples the
        // arrow along with the frame). int.MinValue means "no cursor this frame".
        var cursorOutputX = int.MinValue;
        var cursorOutputY = int.MinValue;
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
            // Which output the duplication below is actually bound to - see the
            // once-a-second target recheck in the loop for why the window handle
            // alone is the wrong thing to compare against.
            var targetMonitor = ResolveTargetMonitor(targetHandle, config);
            duplication = CreateDuplicationFor(device, targetHandle, config, out var desktopBounds);

            var (captureWidth, captureHeight) = isMonitorMode
                ? (desktopBounds.Right - desktopBounds.Left, desktopBounds.Bottom - desktopBounds.Top)
                : GetInitialCropSize(targetHandle, desktopBounds);

            var (outputWidth, outputHeight) = CaptureOutputSize(config, captureWidth, captureHeight);
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;

            // Debounces the crop-size-changed resource rebuild below (staging
            // texture, scaler, GPU crop view) against a single-iteration blip
            // in DwmGetWindowAttribute's reported bounds. Alt-tabbing back into
            // a window plays a compositor restore animation for a few frames
            // (Windows' own alt-tab switcher, unless the user has disabled
            // animations), during which the extended frame bounds genuinely
            // read different sizes iteration to iteration before settling -
            // rebuilding real GPU resources on every one of those transient
            // reads, right as gameplay resumes, is expensive enough on its own
            // to look exactly like the encoder overload this was chasing.
            // Requiring the same new size to repeat a few times before
            // committing to a rebuild costs at most a few iterations' (tens of
            // ms) latency on a genuine resize, which is imperceptible, while
            // skipping the rebuild entirely for a same-frame-or-two blip that
            // never repeats.
            var pendingCropWidth = captureWidth;
            var pendingCropHeight = captureHeight;
            var pendingCropStableCount = 0;
            const int cropStableThreshold = 3;

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
            // CaptureCursor used to disable this whole path ("desktop cursor uses CPU
            // composition"), which quietly cost anyone recording with the cursor on
            // the entire GPU scaler: a 2560x1440 -> 1920x1080 sws_scale measured
            // 10-13ms per frame of a 16.7ms budget at 60fps, so capture stuttered on
            // machines whose GPU was nearly idle. The cursor is a synthetic arrow, not
            // a sampled cursor image, so it does not need the full-resolution BGRA
            // buffer at all - it is drawn into the NV12 frame after the scaled
            // readback instead (see DrawDesktopCursorNv12).
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
            // has focus, common when starting the buffer from ClypDat's own
            // window), the very first frames get encoded straight from that
            // garbage NV12 data before any real capture ever lands in it,
            // which renders as solid green (Y/U/V all near zero decodes to
            // bright green in YUV->RGB). Fill it to black up front instead.
            FillFrameBlack(frame, outputHeight);

            packet = ffmpeg.av_packet_alloc();

            // Keep at most roughly half a second of stale work. A 120-frame queue
            // at 60 FPS turned a short GPU stall into seconds of old frames, then
            // magnified it with catch-up work. Dropping early is recoverable;
            // encoding stale frames is visible lag.
            var encodeQueueCapacity = Math.Clamp(config.FrameRate / 2, 8, 60);
            encodeQueue = new BlockingCollection<EncodeJob>(boundedCapacity: encodeQueueCapacity);
            // Pointer locals can't be captured by a lambda closure directly - cross
            // the thread boundary as nint instead, cast back inside EncodeLoop.
            var encodeCodecContextPtr = (nint)codecContext;
            var encodePacketPtr = (nint)packet;
            var encodeFullSessionFormatContextPtr = (nint)fullSessionFormatContext;
            var encodeFullSessionStreamPtr = (nint)fullSessionStream;
            encodeThread = new Thread(() => EncodeLoop(encodeQueue, encodeCodecContextPtr, encodePacketPtr, encodeFullSessionFormatContextPtr, encodeFullSessionStreamPtr))
            {
                IsBackground = true,
                Name = "ClypDat-NativeEncode"
            };
            try { encodeThread.Priority = ThreadPriority.AboveNormal; }
            catch (Exception error) { AppLog.Error("Native capture: failed to raise encode thread priority (non-fatal)", error); }
            encodeThread.Start();

            var adapterDescription = DescribeAdapter(device);
            AppLog.Info($"Native capture started (DXGI Desktop Duplication): target={(targetHandle != 0 ? "window" : "primary monitor")}, source={captureWidth}x{captureHeight}, output={outputWidth}x{outputHeight}, encoder={encoderName}, adapter={adapterDescription}, preset={config.EncoderPreset}, configFrameRate={config.FrameRate}.");
            SetHealth(new ReplayCaptureHealth("Native", "Desktop Duplication", ReplayCaptureState.Healthy,
                config.FrameRate, 0, 0, 0, 0, 0, 0, encoderName, "Default adapter", string.Empty, DateTime.UtcNow)
            {
                AdapterDescription = adapterDescription,
                EncoderPreset = config.EncoderPreset,
                EncodeQueueCapacity = encodeQueueCapacity
            });
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
            // Accumulated rather than "index * interval", because the interval
            // is no longer fixed for the life of the session - RequestFrameRate
            // can halve it when the encoder cannot keep up. Multiplying a
            // running index by a changed interval would retroactively restate
            // every timestamp already emitted and jump the timeline; adding the
            // current interval each time keeps PTS monotonic and exactly spaced
            // across the change. time_base is microseconds (see the encoder
            // setup) and independent of frame rate, so nothing else moves.
            var nextPtsMicroseconds = 0.0;
            var idealFrameIntervalMicroseconds = 1_000_000.0 / Math.Clamp(config.FrameRate, 15, 240);
            var activeFrameRate = Math.Clamp(config.FrameRate, 15, 240);
            Volatile.Write(ref _requestedFrameRate, activeFrameRate);
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
            // Polling at 1ms wakes 600-1000 times/sec at 60 FPS and burns CPU/GPU
            // work without creating new desktop frames. Keep enough cadence for
            // target pacing, but let the scheduler sleep through most of a frame.
            var acquireTimeoutMs = (uint)Math.Clamp((int)Math.Round(targetFrameInterval.TotalMilliseconds / 2), 1, 8);
            var lastDiagLog = TimeSpan.Zero;
            var lastRingTrim = TimeSpan.Zero;
            var framesSeen = 0;
            var framesSeenSinceLog = 0;
            var framesProcessedSinceLog = 0;
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
            // See the content-write sites above and the pacing gate below -
            // measures how stale frame->data's content actually is at the
            // moment each output frame gets encoded, to find out whether a
            // high-source-fps/lower-target-fps mismatch (e.g. 240->60) is a
            // real, measurable contributor to perceived judder or not, before
            // touching the pacing algorithm itself.
            var lastFrameContentCapturedUtc = MonotonicClock.UtcNow;
            var frameStalenessMs = 0.0;
            var frameStalenessMaxMs = 0.0;
            var frameStalenessCount = 0;
            // Whether anything new has reached frame->data since the last frame
            // was handed to the encoder. False means the next scheduled frame
            // would be a pure duplicate - see the pacing gate below.
            var freshContentSinceLastEncode = false;
            var padsSkippedSinceLog = 0;

            // Watchdog state for a duplication that stops delivering frames
            // without ever reporting AccessLost. Only AccessLost triggered a
            // recreate, so any other persistent AcquireNextFrame failure just
            // hit the 50ms backoff below and retried forever, silently - the
            // HRESULT wasn't even logged. One session sat like that for 105
            // minutes: framesSeen stuck at 1, the pacing gate happily padding
            // that single frame out at 60fps, and the clip saved from it was
            // 61 seconds of one black frame. Recreating the duplication is the
            // first move; if that doesn't take, the D3D device itself is
            // rebuilt, since a device lost underneath us can't produce a
            // working duplication no matter how many times we ask.
            var consecutiveAcquireFailures = 0;
            // An hour back rather than TimeSpan.MinValue: these are only ever
            // used as `stopwatch.Elapsed - x`, and subtracting MinValue
            // overflows TimeSpan outright.
            var lastAcquireFailureLog = TimeSpan.FromHours(-1);
            var lastRealFrameElapsed = TimeSpan.Zero;
            var lastRecoveryAttempt = TimeSpan.FromHours(-1);
            var recoveryAttempts = 0;
            var isStalled = false;
            // ~1s of solid failures at the 50ms transient backoff. Long enough
            // that a genuine desktop-switch blip rides it out untouched.
            const int acquireFailureRecreateThreshold = 20;
            // A game legitimately presenting nothing for this long (paused on a
            // static menu) is possible, so this path only ever recreates the
            // duplication - cheap, and invisible if it wasn't needed.
            var stallRecreateAfter = TimeSpan.FromSeconds(10);
            var recoveryRetryInterval = TimeSpan.FromSeconds(2);
            // Three failed recreates means the problem isn't the duplication.
            const int recoveryAttemptsBeforeDeviceRebuild = 3;

            while (!token.IsCancellationRequested)
            {
                if (stopwatch.Elapsed - lastDiagLog >= TimeSpan.FromSeconds(2))
                {
                    var diagElapsed = Math.Max(0.001, (stopwatch.Elapsed - lastDiagLog).TotalSeconds);
                    lastDiagLog = stopwatch.Elapsed;
                    var n = Math.Max(1, framesEncodedSinceLog);
                    var m = Math.Max(1, iterationsSinceLog);
                    // accumulatedFramesSum is only added to on iterations that
                    // actually delivered a real frame, so framesSeenSinceLog is
                    // its matching denominator. Subtracting zeroPresentSkips from
                    // total iterations (what this used to do) still left every
                    // AcquireNextFrame TIMEOUT in the count - the majority of
                    // iterations at any target below the poll rate - which scaled
                    // avgAccumulatedFrames down by ~2.3x and made a healthy
                    // one-present-per-acquire capture read as 0.43, i.e. as if
                    // over half of all acquires were coming back empty.
                    var realFrameCount = Math.Max(1, framesSeenSinceLog);
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
                    lock (_bufferLock) ringBufferBytes = _ringBufferBytes;
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
                    UpdatePeak(ref _peakQueueDepth, encodeQueue.Count);
                    var frameStalenessDenom = Math.Max(1, frameStalenessCount);
                    AppLog.Debug($"Native capture diag: framesSeen={framesSeen}, framesEncoded={framesEncoded}, ringPackets={_packets.Count}, ringBufferMb={ringBufferMb}, avgCopyMapMs={copyMapMs / n:0.00}, avgScaleMs={scaleMs / n:0.00}, avgQueueMs={encodeMs / n:0.00}, avgEncodeMs={encodeMicrosSinceLog / 1000.0 / encodeCountSinceLog:0.00}, queueDepth={encodeQueue.Count}, droppedFrames={droppedSinceLog}, padsSkipped={padsSkippedSinceLog}, avgWaitMs={waitMs / m:0.00}, avgGetFrameMs={getFrameMs / m:0.00}, avgPreAcquireMs={preAcquireMs / m:0.00}, maxPreAcquireMs={preAcquireMaxMs:0.00}, avgFrameStalenessMs={frameStalenessMs / frameStalenessDenom:0.00}, maxFrameStalenessMs={frameStalenessMaxMs:0.00}, iterations={iterationsSinceLog}, zeroPresentSkips={zeroPresentSkips}, avgAccumulatedFrames={(double)accumulatedFramesSum / realFrameCount:0.00}, maxAccumulatedFrames={accumulatedFramesMax}, avgPresentGapMs={presentGapSumMs / presentGapDenom:0.00}, maxPresentGapMs={presentGapMaxMs:0.00}, managedMb={managedMb}, gen0={GC.CollectionCount(0)}, gen1={GC.CollectionCount(1)}, gen2={GC.CollectionCount(2)}.");
                    var outputFrameRate = framesEncodedSinceLog / diagElapsed;
                    // Judged against the rate actually being targeted, not the
                    // configured one: once the tuner has lowered it, hitting 30
                    // of 30 is healthy, and comparing against the original 60
                    // would report a permanent overload and ratchet forever.
                    var overloaded = droppedSinceLog > 0 || encodeQueue.Count * 4 >= encodeQueueCapacity * 3 ||
                                     (hasCapturedRealFrame && outputFrameRate < activeFrameRate * 0.9);
                    // A stall is worse than an overload and reads nothing like one:
                    // no frames arrive at all, so nothing gets dropped and the queue
                    // stays empty - every overload signal above says "healthy" right
                    // up until the clip comes out frozen. Report it while it is
                    // happening instead, so the UI can say so live rather than the
                    // save-time warning being the first anyone hears of it.
                    if (isStalled) _lastDegradedUtc = DateTime.UtcNow;
                    if (overloaded)
                    {
                        _lastDegradedUtc = DateTime.UtcNow;
                        // Same numbers as the DEBUG diag line above, but at INFO so
                        // an overload shows up in the log a user would actually
                        // send without needing a full diagnostics export - the
                        // debug log is 1-8MB/day and isn't something to ask for
                        // first when triaging "clips are choppy."
                        var recoveryGuidance = string.Equals(config.EncoderPreset, "P1", StringComparison.OrdinalIgnoreCase)
                            ? "P1 is already active; reduce capture resolution or frame rate."
                            : "Try a faster Encoder preset in Settings.";
                        AppLog.Info($"Native capture: overload - dropped {droppedSinceLog} frame(s) in the last {diagElapsed:0.0}s, queue {encodeQueue.Count}/{encodeQueueCapacity}, avgEncodeMs={encodeMicrosSinceLog / 1000.0 / encodeCountSinceLog:0.0}, avgScaleMs={scaleMs / n:0.0}. {recoveryGuidance}");
                    }
                    SetHealth(new ReplayCaptureHealth("Native", "Desktop Duplication",
                        overloaded || isStalled ? ReplayCaptureState.Degraded : ReplayCaptureState.Healthy,
                        activeFrameRate, framesSeenSinceLog / diagElapsed, framesProcessedSinceLog / diagElapsed,
                        outputFrameRate, Math.Max(0, framesEncodedSinceLog - framesProcessedSinceLog), droppedSinceLog, encodeQueue.Count,
                        encoderName, "Default adapter",
                        isStalled ? "Capture stalled - no new frames from the display. Recovering." :
                        overloaded ? "Capture overload. Output may fall below target FPS." : string.Empty,
                        DateTime.UtcNow)
                    {
                        TotalDroppedFrames = Interlocked.Read(ref _totalDroppedFrames),
                        PeakQueueDepth = Volatile.Read(ref _peakQueueDepth),
                        LastDegradedUtc = _lastDegradedUtc,
                        // Stall wins when both are true: no frames are arriving,
                        // so whatever the encoder looks like is a consequence of
                        // that rather than the encode settings being too costly.
                        DegradeReason = isStalled ? ReplayDegradeReason.CaptureStall
                            : overloaded ? ReplayDegradeReason.EncoderOverload
                            : ReplayDegradeReason.None,
                        AdapterDescription = adapterDescription,
                        EncoderPreset = config.EncoderPreset,
                        EncodeQueueCapacity = encodeQueueCapacity
                    });
                    copyMapMs = 0;
                    scaleMs = 0;
                    encodeMs = 0;
                    frameStalenessMs = 0;
                    frameStalenessMaxMs = 0;
                    frameStalenessCount = 0;
                    framesEncodedSinceLog = 0;
                    framesSeenSinceLog = 0;
                    framesProcessedSinceLog = 0;
                    padsSkippedSinceLog = 0;
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

                        // Duplication is per-OUTPUT, not per-window: the window is
                        // followed by cropping each frame, so a new target on the
                        // same monitor needs nothing rebuilt here. This used to tear
                        // down and recreate on every target change regardless, and a
                        // recreate is precisely where things go wrong - DuplicateOutput
                        // returns E_ACCESSDENIED whenever the secure desktop is up or
                        // another process holds the output (one session logged 460 of
                        // those in 45 seconds), and every rebuild is another chance to
                        // land in a duplication that never delivers again. Game
                        // detection flapping between a launcher and the game, or the
                        // game closing and falling back to monitor mode, all resolve
                        // to the same monitor and are now free.
                        var freshMonitor = ResolveTargetMonitor(targetHandle, config);
                        if (freshMonitor != targetMonitor || duplication is null)
                        {
                            targetMonitor = freshMonitor;
                            duplication?.Dispose();
                            // Null out before the recreate attempt - if it throws, `duplication`
                            // must not be left pointing at the just-disposed object, or the next
                            // AcquireNextFrame call below crashes the whole loop with an NRE
                            // instead of just retrying (see the null-guard above the acquire call).
                            duplication = null;
                            try
                            {
                                duplication = CreateDuplicationFor(device, targetHandle, config, out desktopBounds);
                            }
                            catch (Exception error)
                            {
                                AppLog.Error("Native capture: failed to switch DXGI duplication target.", error);
                            }
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
                // every other capture tool (such as ScreenRecorderLib), which
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
                        duplication = CreateDuplicationFor(device, targetHandle, config, out desktopBounds);
                        AppLog.Info("Native capture: DXGI duplication recreated after prior failure.");
                    }
                    catch (Exception error)
                    {
                        AppLog.Error("Native capture: duplication recreate retry failed.", error);
                    }
                    continue;
                }

                var acquireResult = duplication.AcquireNextFrame(acquireTimeoutMs, out var frameInfo, out var desktopResource);
                waitMs += stageStopwatch.Elapsed.TotalMilliseconds;

                var occluded = !isMonitorMode && !IsWindowForegroundAndVisible(targetHandle);

                if (acquireResult.Success)
                {
                    consecutiveAcquireFailures = 0;
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
                            framesSeenSinceLog++;
                            // The watchdog's heartbeat: the duplication just
                            // handed us genuinely new desktop content, which is
                            // the one thing a stalled capture never does.
                            if (isStalled)
                            {
                                isStalled = false;
                                AppLog.Info($"Native capture: frames resumed after a {(stopwatch.Elapsed - lastRealFrameElapsed).TotalSeconds:0.#}s stall.");
                            }
                            lastRealFrameElapsed = stopwatch.Elapsed;
                            recoveryAttempts = 0;

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
                                if (cropWidth == pendingCropWidth && cropHeight == pendingCropHeight)
                                {
                                    pendingCropStableCount++;
                                }
                                else
                                {
                                    pendingCropWidth = cropWidth;
                                    pendingCropHeight = cropHeight;
                                    pendingCropStableCount = 1;
                                }

                                if (pendingCropStableCount >= cropStableThreshold)
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
                                        // The texture the pending crop lived in is gone - the
                                        // replacement holds nothing yet, so there is nothing
                                        // for the next encode tick to convert until a fresh
                                        // present lands in it.
                                        croppedDirty = false;
                                    }

                                    pendingCropStableCount = 0;
                                }
                            }
                            else
                            {
                                pendingCropWidth = captureWidth;
                                pendingCropHeight = captureHeight;
                                pendingCropStableCount = 0;
                            }

                            // Always process every genuinely new present (this used to be
                            // throttled to "at most once per target interval," reasoning that
                            // conversion/readback can't improve on an already-fresh scheduled
                            // frame) - two rounds of tuning that throttle (a phase-lock fix,
                            // then a jitter tolerance) each helped but never eliminated a
                            // real content-loss pattern: real presentation timing comes in
                            // bursts (a short gap right after a long one, confirmed via
                            // maxPresentGapMs swinging several ms around the mean even during
                            // steady gameplay), and a reactive "was it due YET" gate checked
                            // only at each present's own arrival moment permanently drops
                            // whichever presents land during a burst, no matter how generous
                            // the tolerance - there's no queue to catch up from later. That
                            // capped real output at ~90-93% of the source's own rate regardless
                            // of target fps (60/90/120/144 all equally affected, since the
                            // mechanism has nothing to do with the specific numbers involved).
                            // Scale/copy itself is cheap (~1ms, see avgScaleMs), so keeping
                            // frame->data always maximally fresh costs little even at a
                            // V-Sync-off source presenting in the hundreds of fps - the actual
                            // rate cap that matters (respecting the user's chosen target)
                            // still happens below: the fixed-rate encode-tick loop already
                            // throttles correctly regardless of how fresh its input is, where
                            // a skip is harmless (the next check just finds the still-fresh
                            // frame) rather than a permanently lost capture.
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
                                if (useGpuScale)
                                {
                                    // Crop only. This is the one part that genuinely has to
                                    // run per present, because the duplication frame is
                                    // released the moment this block exits - everything
                                    // downstream of it (the scale/convert Blt, the readback
                                    // copy and the CPU-side plane copy) reads croppedTexture
                                    // instead, so it can wait for a tick that will actually
                                    // encode.
                                    //
                                    // It used to all run here, once per present. At a target
                                    // well below the source's own rate that is mostly wasted
                                    // work: a 240fps source feeding a 60fps target converted
                                    // roughly four frames for every one that was ever
                                    // encoded, and measured ~24% of a 4070 Ti doing it (the
                                    // ~1ms conversion, 232x/second). The comment that called
                                    // this "cheap (~1ms, see avgScaleMs)" was reading its own
                                    // metric wrong - avgScaleMs divides by frames ENCODED,
                                    // not by the far larger number of presents it actually
                                    // ran on, so the true cost was understated by exactly the
                                    // source/target ratio.
                                    stageStopwatch.Restart();
                                    using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                                    {
                                        var box = new Vortice.Mathematics.Box(cropLeft, cropTop, 0, cropLeft + captureWidth, cropTop + captureHeight, 1);
                                        device.ImmediateContext.CopySubresourceRegion(croppedTexture, 0, 0, 0, 0, desktopTexture, 0, box);
                                    }
                                    copyMapMs += stageStopwatch.Elapsed.TotalMilliseconds;

                                    // Same screen->crop conversion the CPU path does, then scaled
                                    // into output pixels so the readback side can draw straight
                                    // into the NV12 frame without needing the crop rect.
                                    if (config.CaptureCursor && GetCursorPos(out var gpuCursor))
                                    {
                                        var cropX = gpuCursor.X - desktopBounds.Left - cropLeft;
                                        var cropY = gpuCursor.Y - desktopBounds.Top - cropTop;
                                        cursorOutputX = (int)((long)cropX * outputWidth / captureWidth);
                                        cursorOutputY = (int)((long)cropY * outputHeight / captureHeight);
                                    }
                                    else
                                    {
                                        cursorOutputX = int.MinValue;
                                        cursorOutputY = int.MinValue;
                                    }

                                    croppedDirty = true;
                                }
                                else
                                {
                                    ffmpeg.av_frame_make_writable(frame);
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
                                        if (config.CaptureCursor && GetCursorPos(out var cursor))
                                        {
                                            DrawDesktopCursor((byte*)mapped.DataPointer, (int)mapped.RowPitch, captureWidth, captureHeight,
                                                cursor.X - desktopBounds.Left - cropLeft,
                                                cursor.Y - desktopBounds.Top - cropTop);
                                        }
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

                                // Real-world moment this content actually landed in frame->data -
                                // see the pacing gate below (frameStalenessMs) for why: at a source
                                // fps well above target (240 ->60 here), whatever's newest in
                                // frame->data when the pacing gate fires can still be up to one
                                // source-interval stale, varying iteration to iteration since the
                                // two clocks (source presents, target-fps pacing) run free-running
                                // and unsynchronized - a plausible source of visible judder despite
                                // every output frame being unique and perfectly PTS-spaced.
                                lastFrameContentCapturedUtc = MonotonicClock.UtcNow;
                                Volatile.Write(ref _lastRealContentTicks, lastFrameContentCapturedUtc.Ticks);
                                // Set on both the GPU-scale and CPU-copy paths, unlike
                                // croppedDirty which only the GPU one uses - the pacing
                                // gate needs to know "is the next scheduled frame a pad"
                                // regardless of which path produced the content.
                                freshContentSinceLastEncode = true;
                                framesProcessedSinceLog++;
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
                        duplication = CreateDuplicationFor(device, targetHandle, config, out desktopBounds);
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
                        // Counted and logged, because "transient" turned out to be
                        // an assumption: the same non-AccessLost code can repeat
                        // forever, and this branch used to swallow it silently
                        // while the watchdog below had nothing to go on. Rate-limited
                        // so a permanent failure doesn't write 20 lines a second.
                        consecutiveAcquireFailures++;
                        if (stopwatch.Elapsed - lastAcquireFailureLog >= TimeSpan.FromSeconds(5))
                        {
                            lastAcquireFailureLog = stopwatch.Elapsed;
                            AppLog.Info($"Native capture: AcquireNextFrame failed with 0x{acquireResult.Code:X8} ({consecutiveAcquireFailures} in a row).");
                        }
                        Thread.Sleep(50);
                    }
                    // WaitTimeout: genuinely nothing new from the source yet this
                    // cycle - fall through to the pacing gate below exactly like a
                    // successful-but-occluded frame would, so frame->data's last
                    // real content still gets duplicate-encoded on schedule instead
                    // of the encoded frame rate just falling behind.
                }

                // Stall watchdog. Two ways in: AcquireNextFrame erroring solidly
                // (unambiguous - something is broken), or simply no new desktop
                // content for a long time while the game window IS foreground.
                // The second is a soft signal, since a genuinely static screen
                // looks identical from here, so it only ever costs a duplication
                // recreate - a few milliseconds, and harmless if it wasn't needed.
                if (hasCapturedRealFrame && !occluded &&
                    (consecutiveAcquireFailures >= acquireFailureRecreateThreshold ||
                     stopwatch.Elapsed - lastRealFrameElapsed >= stallRecreateAfter) &&
                    stopwatch.Elapsed - lastRecoveryAttempt >= recoveryRetryInterval)
                {
                    var stalledSeconds = (stopwatch.Elapsed - lastRealFrameElapsed).TotalSeconds;
                    if (!isStalled)
                    {
                        isStalled = true;
                        AppLog.Info($"Native capture: no new frames for {stalledSeconds:0.#}s (acquire failures={consecutiveAcquireFailures}) - recovering.");
                    }

                    lastRecoveryAttempt = stopwatch.Elapsed;
                    recoveryAttempts++;

                    // Past a few failed recreates the duplication isn't the
                    // problem - the device under it is. Rebuild that too, along
                    // with everything bound to it. The encoder, ring buffer and
                    // Full Session writer are deliberately left alone: they hold
                    // the recording's history, and none of them depend on D3D.
                    var rebuildDevice = recoveryAttempts > recoveryAttemptsBeforeDeviceRebuild;
                    try
                    {
                        if (rebuildDevice)
                        {
                            var newDevice = CreateD3D11Device();
                            IDXGIOutputDuplication? newDuplication = null;
                            ID3D11Texture2D? newStaging = null;
                            try
                            {
                                newDuplication = CreateDuplicationFor(newDevice, targetHandle, config, out desktopBounds);
                                newStaging = CreateStagingTexture(newDevice, captureWidth, captureHeight);
                            }
                            catch
                            {
                                newStaging?.Dispose();
                                newDuplication?.Dispose();
                                newDevice.Dispose();
                                throw;
                            }

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
                            device.Dispose();

                            inputView = null;
                            croppedTexture = null;
                            outputView = null;
                            nv12StagingRing = null;
                            nv12Output = null;
                            videoProcessor = null;
                            vpEnumerator = null;
                            videoContext = null;
                            videoDevice = null;

                            device = newDevice;
                            duplication = newDuplication;
                            staging = newStaging;

                            // Same best-effort as the initial setup: if the GPU
                            // path can't be rebuilt, the CPU sws_scale fallback
                            // below still works off `staging` alone.
                            try
                            {
                                if (config.CaptureCursor) throw new NotSupportedException("Desktop cursor uses CPU composition.");
                                (videoDevice, videoContext, vpEnumerator, videoProcessor, nv12Output, nv12StagingRing, outputView) =
                                    CreateGpuScaler(device, captureWidth, captureHeight, outputWidth, outputHeight, config.FrameRate);
                                (croppedTexture, inputView) = CreateGpuCropInputView(device, videoDevice, vpEnumerator, captureWidth, captureHeight);
                                useGpuScale = true;
                            }
                            catch (Exception error)
                            {
                                AppLog.Info($"Native capture: GPU downscale unavailable after device rebuild, falling back to CPU scale: {error.Message}");
                                useGpuScale = false;
                            }
                            nv12StagingIndex = 0;
                            nv12RingWritten = 0;
                            // Every texture the pending crop referred to belongs to the
                            // old device and has just been replaced.
                            croppedDirty = false;

                            AppLog.Info("Native capture: D3D device rebuilt after a stalled duplication.");
                        }
                        else
                        {
                            duplication?.Dispose();
                            duplication = null;
                            duplication = CreateDuplicationFor(device, targetHandle, config, out desktopBounds);
                            AppLog.Info($"Native capture: DXGI duplication recreated after a stall (attempt {recoveryAttempts}).");
                        }

                        consecutiveAcquireFailures = 0;
                    }
                    catch (Exception error)
                    {
                        AppLog.Error($"Native capture: stall recovery failed (attempt {recoveryAttempts}, rebuildDevice={rebuildDevice}).", error);
                    }

                    // The recreate above can leave `duplication` null on failure;
                    // the null-guard at the top of the loop retries it, and
                    // dereferencing it below would crash the whole session.
                    if (duplication is null) continue;
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
                    // Start the stall watchdog's clock here, not at loop start -
                    // this flips the moment the window first has focus, which can
                    // be well before the first real frame lands, and the watchdog
                    // would otherwise read that whole wait as a stall.
                    lastRealFrameElapsed = stopwatch.Elapsed;
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
                // Shared tail of both pacing modes below: force a keyframe on
                // schedule, clone frame (already carrying whatever pts/pict_type
                // the caller just set) and hand it to EncodeLoop. Factored out
                // since the fixed-rate and adaptive-rate branches only differ in
                // WHEN/how they decide to call this, not in what encoding a
                // scheduled frame actually does.
                unsafe void EncodeScheduledFrame()
                {
                    // Convert whatever the latest present cropped, but only now that a
                    // frame is actually being encoded - see the crop block above for why
                    // this moved off the per-present path. Nothing new since the last
                    // encode means frame->data still holds the right content and this is
                    // a deliberate duplicate, exactly like the occlusion freeze.
                    //
                    // Reads back with DoNotWait, newest ready slot first, and never
                    // blocks. A fixed "map the slot from N ticks ago" pairing has to
                    // pick one number for two things it cannot satisfy at once: small
                    // enough that frame->data isn't needlessly stale, large enough that
                    // the copy has finished even when the GPU is running late. Two slots
                    // gave the copy exactly one encode interval, and a 4K game spiking
                    // blew straight through that - the Map then blocked, which is what
                    // turned a brief GPU spike into avgScaleMs of 25-92ms, a full encode
                    // queue and ~100 dropped frames per 2s window.
                    //
                    // Asking instead means staleness stays at one interval whenever the
                    // GPU is keeping up, and degrades to two or three only while it
                    // isn't, rather than the whole pipeline stalling. If nothing is
                    // ready at all, frame->data keeps its previous content and this tick
                    // encodes a duplicate - the same graceful outcome as the occlusion
                    // freeze, and far cheaper than waiting.
                    if (useGpuScale && croppedDirty)
                    {
                        stageStopwatch.Restart();
                        var stream = new VideoProcessorStream { Enable = true, InputSurface = inputView };
                        videoContext!.VideoProcessorBlt(videoProcessor, outputView, 0, 1, new[] { stream });
                        var ringLength = nv12StagingRing!.Length;
                        var currentRingIndex = nv12StagingIndex;
                        device.ImmediateContext.CopyResource(nv12StagingRing[currentRingIndex], nv12Output);
                        if (nv12RingWritten < ringLength) nv12RingWritten++;
                        copyMapMs += stageStopwatch.Elapsed.TotalMilliseconds;

                        stageStopwatch.Restart();
                        // k=1 is the slot written on the previous tick (freshest of the
                        // finished ones), counting back to the oldest still held.
                        for (var k = 1; k < nv12RingWritten; k++)
                        {
                            var candidate = ((currentRingIndex - k) % ringLength + ringLength) % ringLength;
                            var mapResult = device.ImmediateContext.Map(
                                nv12StagingRing[candidate], 0u, MapMode.Read, MapFlags.DoNotWait, out var mapped);
                            // DXGI_ERROR_WAS_STILL_DRAWING - this slot's copy has not
                            // landed yet, so try an older one rather than wait on it.
                            if (mapResult.Failure) continue;

                            try
                            {
                                ffmpeg.av_frame_make_writable(frame);
                                CopyNv12PlanesToFrame(mapped, outputWidth, outputHeight, frame);
                                if (cursorOutputX != int.MinValue)
                                {
                                    DrawDesktopCursorNv12(frame, outputWidth, outputHeight, cursorOutputX, cursorOutputY);
                                }
                            }
                            finally
                            {
                                device.ImmediateContext.Unmap(nv12StagingRing[candidate], 0);
                            }

                            break;
                        }
                        scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;

                        nv12StagingIndex = (currentRingIndex + 1) % ringLength;
                        croppedDirty = false;
                    }

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
                    // Diagnostic only (see lastFrameContentCapturedUtc's declaration) -
                    // how old frame->data's content already was at the moment this
                    // output tick decided to encode it.
                    var staleness = (MonotonicClock.UtcNow - lastFrameContentCapturedUtc).TotalMilliseconds;
                    frameStalenessMs += staleness;
                    if (staleness > frameStalenessMaxMs) frameStalenessMaxMs = staleness;
                    frameStalenessCount++;

                    stageStopwatch.Restart();
                    var clonedFrame = ffmpeg.av_frame_clone(frame);
                    if (clonedFrame is null)
                    {
                        AppLog.Error("Native capture: av_frame_clone failed, dropping a frame.");
                        Interlocked.Increment(ref _encodeDroppedCount);
                        Interlocked.Increment(ref _totalDroppedFrames);
                    }
                    else if (!encodeQueue.TryAdd(new EncodeJob((nint)clonedFrame, MonotonicClock.UtcNow)))
                    {
                        // Queue's genuinely full - the encoder can't keep pace even
                        // decoupled, not just a transient stall. Drop rather than
                        // block (defeats the whole point) or grow unbounded.
                        var droppedFrame = clonedFrame;
                        ffmpeg.av_frame_free(&droppedFrame);
                        Interlocked.Increment(ref _encodeDroppedCount);
                        Interlocked.Increment(ref _totalDroppedFrames);
                    }
                    else
                    {
                        // PTS advances above once per scheduled frame. It must not
                        // advance again here: doing both doubles every successful
                        // frame's duration and turns a 60 FPS clip into ~30 FPS.
                        framesEncoded++;
                        framesEncodedSinceLog++;
                    }
                    encodeMs += stageStopwatch.Elapsed.TotalMilliseconds;
                }

                // Picked up here rather than only at session start: when the
                // encoder cannot sustain the configured rate even at the
                // cheapest preset, EncoderTuningService lowers it live instead
                // of letting a third of the frames go in the bin. Only the
                // pacing interval changes - the encoder, the scaler and the
                // stream parameters are all untouched, so packets from before
                // and after the change still mux into one file.
                var requestedFrameRate = Volatile.Read(ref _requestedFrameRate);
                if (requestedFrameRate != activeFrameRate && requestedFrameRate > 0)
                {
                    activeFrameRate = requestedFrameRate;
                    targetFrameInterval = TimeSpan.FromSeconds(1.0 / activeFrameRate);
                    idealFrameIntervalMicroseconds = 1_000_000.0 / activeFrameRate;
                    acquireTimeoutMs = (uint)Math.Clamp((int)Math.Round(targetFrameInterval.TotalMilliseconds / 2), 1, 8);
                    SetHealth(_health with { TargetFrameRate = activeFrameRate, UpdatedUtc = DateTime.UtcNow });
                    AppLog.Info($"Native capture: target frame rate now {activeFrameRate} fps.");
                }

                if (hasCapturedRealFrame)
                {
                // At most 250 ms of duplicate work after a stall. Longer bursts
                // refill a saturated queue with stale copies and make recovery
                // worse than dropping the missed interval.
                var catchUpFramesRemaining = Math.Clamp(activeFrameRate / 4, 4, 60);
                while (stopwatch.Elapsed - lastEncodedAt >= targetFrameInterval)
                {
                    if (catchUpFramesRemaining-- <= 0)
                    {
                        AppLog.Info($"Native capture: pacing gap of {(stopwatch.Elapsed - lastEncodedAt).TotalSeconds:0.0}s exceeded catch-up cap - snapping timeline forward instead of padding with duplicate frames.");
                        lastEncodedAt = stopwatch.Elapsed;
                        break;
                    }
                    lastEncodedAt += targetFrameInterval;

                    // An ideal, constant-rate timestamp (frame index * the exact
                    // target interval) instead of real elapsed time - the file's
                    // computed average frame rate (what File Explorer/players
                    // show) is then EXACTLY the configured target by construction,
                    // instead of a close-but-jittery approximation from real
                    // scheduler timing. Audio alignment doesn't use this - it gets
                    // its own real wall-clock timestamp below, specifically so
                    // idealizing video's timeline can't reintroduce the audio-sync
                    // bug that was just fixed.
                    frame->pts = (long)Math.Round(nextPtsMicroseconds);
                    nextPtsMicroseconds += idealFrameIntervalMicroseconds;

                    // A pad (nothing new since the last encode) submitted into
                    // an already-backed-up queue is the worst frame to spend
                    // encoder time on: it adds no information, and it pushes the
                    // real frame behind it further back. The friend's Iris Xe
                    // logs show exactly this - framesEncoded running 2.7x
                    // framesSeen while the queue sat pinned at 30/30 and output
                    // collapsed to 18.8fps.
                    //
                    // The timeline still advances (lastEncodedAt and
                    // encodedFrameIndex both moved above), so this reads as a
                    // dropped frame in the output rather than a slower clip -
                    // same shape as the catch-up cap right above, which already
                    // decided that under stall conditions honesty beats padding.
                    // Only ever skipped while the queue is genuinely deep, so
                    // steady-state constant-frame-rate output is unchanged.
                    if (!freshContentSinceLastEncode && encodeQueue.Count * 2 >= encodeQueueCapacity)
                    {
                        padsSkippedSinceLog++;
                        continue;
                    }

                    EncodeScheduledFrame();
                    freshContentSinceLastEncode = false;
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
            SetHealth(_health with { State = ReplayCaptureState.Failed, LastFailure = error.Message, UpdatedUtc = DateTime.UtcNow });
            ready.TrySetException(error);
            _sessionActive = false;

            // The session is dead, so nothing will ever consume these captures.
            // StopAsync can't do this cleanup on our behalf - it early-returns
            // on !_sessionActive, which we just cleared - so without stopping
            // here the audio pipeline kept capturing (and re-resolving its
            // route on every device change) indefinitely behind a capture loop
            // that no longer exists.
            try
            {
                _audio.Stop(deleteCaptureFiles: true);
            }
            catch (Exception audioError)
            {
                AppLog.Error("Native capture: audio shutdown after capture-loop failure failed.", audioError);
            }

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
                        ClipInfoSidecar.Save(finalizeConfig.LibraryFolder, finalPath, new ClipInfo(immediateGameName, null, $"Session - {immediateGameName}", startWallUtc, CaptureSource: finalizeConfig.CaptureSource));
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
            if (_health.State != ReplayCaptureState.Failed)
            {
                SetHealth(_health with { State = ReplayCaptureState.Stopped, UpdatedUtc = DateTime.UtcNow });
            }
        }
    }

    private static unsafe void FillFrameBlack(AVFrame* frame, int height)
    {
        // Y=0, not 16: the frame is signalled full-range (see CreateEncoder's
        // AVCOL_RANGE_JPEG), where black is 0 and 16 is a visible dark grey.
        // 16 is limited-range black and was correct before that change.
        var ySize = (uint)(frame->linesize[0] * height);
        System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned((void*)frame->data[0], 0, ySize);

        var uvHeight = (height + 1) / 2;
        var uvSize = (uint)(frame->linesize[1] * uvHeight);
        System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned((void*)frame->data[1], 128, uvSize);
    }

    private static unsafe SwsContext* CreateScaler(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var swsContext = ffmpeg.sws_getContext(
            sourceWidth, sourceHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
            outputWidth, outputHeight, AVPixelFormat.AV_PIX_FMT_NV12,
            // Lanczos, not bilinear. Measured on a synthetic 2:1 downscale of
            // real capture content, bilinear scores SSIM 0.9952 (23.2dB) against
            // lanczos' 0.9987 (28.8dB) - a 5.6dB detail loss purely from the
            // resampling kernel, which is exactly the "looks like a lower
            // resolution" symptom. ACCURATE_RND drops the rounding shortcuts
            // that would otherwise give some of that back.
            (int)(SwsFlags.SWS_LANCZOS | SwsFlags.SWS_ACCURATE_RND), null, null, null);
        if (swsContext is null) throw new InvalidOperationException("sws_getContext failed.");

        // Desktop Duplication's BGRA capture is full-range (0-255) - without
        // this, sws_scale's default conversion targets studio/limited range
        // (16-235) NV12 output, crushing blacks/whites once played back at
        // the correct (full) range. BT.709 coefficients (not swscale's BT.601
        // default) because that's what the encoder now tags the output as, and
        // what any player assumes for HD regardless - converting with 601 and
        // being decoded as 709 is a real colour shift.
        var coefficientsPtr = ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
        var coefficients = new int_array4();
        coefficients.UpdateFrom(new[] { coefficientsPtr[0], coefficientsPtr[1], coefficientsPtr[2], coefficientsPtr[3] });
        ffmpeg.sws_setColorspaceDetails(swsContext, in coefficients, 1 /* full */, in coefficients, 1 /* full */, 0, 1 << 16, 1 << 16);

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
            // OptimalQuality, not PlaybackNormal: this is the only lever the
            // D3D11 Video Processor exposes over which scaling kernel the
            // driver picks (no caps are queryable that would let us pick one
            // outright), and a 2:1 downscale is exactly where that choice shows.
            Usage = VideoUsage.OptimalQuality
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

        // Desktop Duplication's input is full-range (0-255) RGB, and left at
        // its driver defaults the Video Processor's NV12 output nominally
        // targets studio/limited range (16-235) - same crushed blacks/whites
        // problem as the CPU sws_scale path (see CreateScaler), just via a
        // different API. RGB_Range/Usage stay at their already-correct defaults
        // (full-range RGB in); Nominal_Range=1 is 0-255/full per
        // D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE, and YCbCr_Matrix=1 is BT.709 -
        // matching what the encoder now tags the output as, rather than the
        // BT.601 default that silently disagreed with how every player reads
        // an HD stream.
        var colorSpace = new VideoProcessorColorSpace { Nominal_Range = 1, YCbCr_Matrix = 1 };
        videoContext.VideoProcessorSetStreamColorSpace(processor, 0, colorSpace);
        videoContext.VideoProcessorSetOutputColorSpace(processor, colorSpace);

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

        // Four slots, not two. Depth here buys tolerance for the GPU running
        // late: the readback of a slot can only be picked up once its copy has
        // actually finished, and under real contention (a 4K game spiking) that
        // took longer than the single encode interval two slots allow, which
        // showed up as the readback blocking and cascading into dropped frames.
        // Costs three extra NV12 surfaces - a few MB at 1080p.
        var nv12StagingRing = new[]
        {
            CreateNv12StagingTexture(device, outputWidth, outputHeight),
            CreateNv12StagingTexture(device, outputWidth, outputHeight),
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
    // The output a given target resolves to. Kept separate from
    // CreateDuplicationFor so the capture loop can ask "would this target need a
    // different duplication?" without building one to find out.
    private static nint ResolveTargetMonitor(nint targetHandle, ReplayBufferConfig config)
    {
        if (targetHandle != 0) return MonitorFromWindow(targetHandle, MONITOR_DEFAULTTONEAREST);
        if (string.Equals(config.CaptureSource, "Desktop", StringComparison.OrdinalIgnoreCase))
        {
            var monitor = DesktopMonitorService.Resolve(config.CaptureMonitorDeviceName);
            return MonitorFromPoint(new PointStruct { X = monitor.X + Math.Max(1, monitor.Width / 2), Y = monitor.Y + Math.Max(1, monitor.Height / 2) }, MONITOR_DEFAULTTONEAREST);
        }
        return GetPrimaryMonitorHandle();
    }

    // Desktop Duplication reports pointer movement separately from pixels. Keep
    // native desktop captures truthful with a small high-contrast arrow on the
    // CPU fallback path; game capture intentionally never enters this path.
    private static unsafe void DrawDesktopCursor(byte* pixels, int stride, int width, int height, int x, int y)
    {
        for (var row = 0; row < 15; row++)
        {
            for (var column = 0; column < 12; column++)
            {
                var inside = column <= row / 2 || (row > 7 && column >= 3 && column <= 6 && row - 7 <= column - 2);
                if (!inside) continue;
                var px = x + column;
                var py = y + row;
                if (px < 0 || py < 0 || px >= width || py >= height) continue;
                var edge = column == 0 || column == row / 2 || row == 0;
                var target = pixels + py * stride + px * 4;
                target[0] = edge ? (byte)0 : (byte)245;
                target[1] = edge ? (byte)0 : (byte)245;
                target[2] = edge ? (byte)0 : (byte)245;
                target[3] = 255;
            }
        }
    }

    // Same arrow as DrawDesktopCursor, drawn straight into the already-scaled NV12
    // frame so the GPU scaler path can keep the cursor without falling back to a
    // full-resolution CPU sws_scale. The shape is deliberately kept identical to the
    // CPU path's so a recording looks the same whichever path a machine takes.
    //
    // The arrow is greyscale, so only the Y plane carries it and the chroma pair is
    // pushed to neutral - no colour conversion is involved. Y uses limited range (16
    // = black, 235 = white) to match what the video processor writes for the rest of
    // the frame; using 0/255 here would make the arrow clip against it.
    private static unsafe void DrawDesktopCursorNv12(AVFrame* frame, int width, int height, int x, int y)
    {
        var yPlane = frame->data[0];
        var yStride = frame->linesize[0];
        var uvPlane = frame->data[1];
        var uvStride = frame->linesize[1];

        for (var row = 0; row < 15; row++)
        {
            for (var column = 0; column < 12; column++)
            {
                var inside = column <= row / 2 || (row > 7 && column >= 3 && column <= 6 && row - 7 <= column - 2);
                if (!inside) continue;
                var px = x + column;
                var py = y + row;
                if (px < 0 || py < 0 || px >= width || py >= height) continue;
                var edge = column == 0 || column == row / 2 || row == 0;
                yPlane[py * yStride + px] = edge ? (byte)16 : (byte)235;

                // One chroma sample covers a 2x2 luma block, so this writes the same
                // neutral pair repeatedly for interior pixels. Cheaper than tracking
                // which blocks were already touched, at 180 pixels a frame.
                var uv = uvPlane + (py / 2) * uvStride + (px / 2) * 2;
                uv[0] = 128;
                uv[1] = 128;
            }
        }
    }

    // The GPU's own name ("NVIDIA GeForce RTX 4070 Ti"), for diagnostics and for
    // keying any learned per-machine encoder tuning - a learned value has no
    // business following the user onto different hardware. Best-effort: this is
    // reporting only, so a failure here must never take capture down with it.
    private static string DescribeAdapter(ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
            var description = adapter.Description.Description;
            return string.IsNullOrWhiteSpace(description) ? "Unknown adapter" : description.Trim();
        }
        catch (Exception error)
        {
            AppLog.Info($"Native capture: could not read adapter description ({error.Message}).");
            return "Unknown adapter";
        }
    }

    private static IDXGIOutputDuplication CreateDuplicationFor(ID3D11Device device, nint targetHandle, ReplayBufferConfig config, out Vortice.RawRect desktopBounds)
    {
        var monitorHandle = ResolveTargetMonitor(targetHandle, config);

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
            // Pooled, not freshly allocated. This runs once per encoded packet -
            // 60 times a second for as long as a buffer is armed - and the
            // arrays live long enough to be promoted before the ring trims them
            // away, which is the worst possible shape for the GC: a real session
            // walked the managed heap from 23MB to 281MB across 20 gen2
            // collections, and one of those collections shows up in the log as a
            // 714ms capture stall.
            var data = ArrayPool<byte>.Shared.Rent(packet->size);
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
                _packets.Add(new RingPacket(data, packet->size, packet->pts, isKeyframe, realWallClockUtc));
                _ringBufferBytes += packet->size;
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
            tempVideoPath = Path.Combine(Path.GetTempPath(), $"clypdat-full-session-video-{Guid.NewGuid():N}.mp4");

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
                muxArgs.AddRange(new[] { "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("Native Full Session")}", muxOutputPath });
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
                ClipInfoSidecar.Save(config.LibraryFolder, finalOutputPath, new ClipInfo(gameDisplayName, null, $"Session - {gameDisplayName}", sessionStartWallUtc, CaptureSource: config.CaptureSource));
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

    // Deletes the oldest ClypDat-recorded session files (identified by their own
    // sidecar's "... Full Session" FileTitle - never touches clips or files
    // ClypDat didn't write) until the library's VODs tree fits the configured
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

    // Hands [index, index + count) back to the pool. Callers must hold
    // _bufferLock and must remove the entries themselves - this only releases
    // the payloads and keeps _ringBufferBytes in step.
    //
    // Safe to recycle unconditionally because nothing outside the ring ever
    // holds one of these arrays: SaveReplayAsync takes its own copy of the
    // bytes before it lets go of the lock (see CopyWindowUnderLock).
    private void ReturnPooledPackets(int index, int count)
    {
        for (var i = index; i < index + count; i++)
        {
            var packet = _packets[i];
            _ringBufferBytes -= packet.Length;
            ArrayPool<byte>.Shared.Return(packet.Data);
        }
    }

    // Selects the save window and copies its bytes out of the ring, both under
    // the buffer lock.
    //
    // The copy is what lets the ring pool its payloads at all. TrimRingBuffer
    // keeps running once a second for the entire multi-second life of a save,
    // so handing the remux references into the ring would let it read arrays
    // that had already been recycled into newer packets - garbage in the middle
    // of the clip, and only under load. One transient copy per save (a
    // user-initiated, already-expensive operation) buys back 60 allocations a
    // second in steady state.
    //
    // Selection runs inside the lock rather than off a lock-free snapshot
    // because the copy has to be in there anyway. These are plain index scans
    // over value fields, not the LINQ pipeline that made holding this lock
    // expensive before.
    private RingPacket[] CopyWindowUnderLock(DateTime requestedStartUtc, DateTime requestedEndUtc)
    {
        lock (_bufferLock)
        {
            if (_packets.Count == 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

            // Saving from a keyframe immediately before the requested event
            // keeps the remux playable while still producing an event-sized clip.
            var startIndex = -1;
            for (var i = _packets.Count - 1; i >= 0; i--)
            {
                if (_packets[i].WallClockUtc <= requestedStartUtc && _packets[i].IsKeyframe) { startIndex = i; break; }
            }
            if (startIndex < 0) startIndex = _packets.FindIndex(packet => packet.IsKeyframe);
            if (startIndex < 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

            var endIndex = -1;
            for (var i = _packets.Count - 1; i >= 0; i--)
            {
                if (_packets[i].WallClockUtc <= requestedEndUtc) { endIndex = i; break; }
            }
            if (endIndex < startIndex) throw new InvalidOperationException("The requested replay window is no longer available.");

            var window = new RingPacket[endIndex - startIndex + 1];
            for (var i = 0; i < window.Length; i++)
            {
                var source = _packets[startIndex + i];
                // Exact-sized, so the copy's Data.Length and Length agree and
                // it is never handed back to the pool by mistake.
                var data = new byte[source.Length];
                Array.Copy(source.Data, data, source.Length);
                window[i] = source with { Data = data };
            }

            return window;
        }
    }

    private void TrimRingBuffer(DateTime? fullSessionStartUtc)
    {
        var cutoff = MonotonicClock.UtcNow - Duration - TimeSpan.FromSeconds(5);
        lock (_bufferLock)
        {
            var removeCount = 0;
            while (removeCount < _packets.Count && _packets[removeCount].WallClockUtc < cutoff) removeCount++;
            if (removeCount > 0)
            {
                ReturnPooledPackets(0, removeCount);
                _packets.RemoveRange(0, removeCount);
            }

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
                    // Length, not Data.Length - see RingPacket. These particular
                    // packets came from CopyWindowUnderLock and so are already
                    // exact-sized, but reading the field keeps this correct if
                    // a pooled packet ever reaches here.
                    ffmpeg.av_new_packet(packet, ringPacket.Length);
                    Marshal.Copy(ringPacket.Data, 0, (IntPtr)packet->data, ringPacket.Length);
                    packet->pts = packet->dts = ringPacket.PtsMs - basePts;
                    packet->stream_index = stream->index;
                    if (ringPacket.IsKeyframe) packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;
                    ffmpeg.av_interleaved_write_frame(formatContext, packet);
                    ffmpeg.av_packet_unref(packet);
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
    // equivalent via the AMF SDK, h264_qsv is Intel's via Quick Sync; libx264 is the
    // last-resort CPU fallback so capture still works even with no usable hardware
    // encoder at all.
    private static readonly string[] EncoderCandidates = { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" };

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
    // slower preset can no longer stall AcquireNextFrame itself - but it can
    // still cost real content: p4 measured avgEncodeMs of 16-28ms/frame under
    // real sustained gameplay load (vs. a target budget well under that),
    // which filled EncodeLoop's queue to its cap and started genuinely
    // dropping frames (confirmed via droppedFrames actually incrementing in
    // Native capture diag, e.g. 110 dropped in one 2s window) - real missing
    // content, not just a compression-quality artifact. p2 is the compromise
    // between p1's motion-compression softness and p4's encode cost. Watch
    // queueDepth/droppedFrames after any future preset change - that pair is
    // the actual signal for whether a preset is sustainable under real load,
    // not just a quick idle-desktop test. Applied to priv_data before
    // avcodec_open2 - these are encoder-specific options, not real
    // AVCodecContext fields, so they have to land before open, not after.
    // Best-effort: an unsupported option name just logs and moves on instead
    // of failing the whole encoder open, since exact option support varies by
    // ffmpeg build/driver version.
    // The encoder settings come straight from user input now, so they're
    // clamped here rather than trusted. Bitrate doubles as the ring buffer's
    // memory bound: the buffer lives entirely in RAM, so a 60s 1080p60 buffer
    // costs roughly 125MB at 16Mbps and scales linearly from there. Only
    // Constant bitrate mode takes a user bitrate value now - Constant quality's
    // ceiling is always derived (see MaxBitrate below).
    private static bool IsConstantBitrate(ReplayBufferConfig config) =>
        string.Equals(config.RateControlMode, "Constant bitrate", StringComparison.OrdinalIgnoreCase);

    // 1-51 is the H.264 quantiser's own range, so this is the full span
    // the encoders actually accept rather than an arbitrary narrowing.
    // 0 is excluded deliberately: NVENC reads cq=0 as "auto", which would
    // silently turn constant quality OFF rather than mean "best possible".
    private static int ConstantQualityTarget(ReplayBufferConfig config) => Math.Clamp(config.ConstantQuality, 1, 51);

    // Constant bitrate: the user's field is the ceiling by definition (it IS
    // the target). Constant quality has no user-facing ceiling input anymore -
    // derive a generous one from the same resolution/fps estimate CaptureBitrate
    // uses, so a burst can still borrow bits without an unbounded ring buffer.
    private static long MaxBitrate(ReplayBufferConfig config) =>
        IsConstantBitrate(config)
            ? Math.Clamp(config.MaxBitrateMbps, 5, 1000) * 1_000_000L
            : Math.Clamp(CaptureBitrate(config) * 2L, 8_000_000L, 160_000_000L);

    // "P3" -> "p3". Anything unrecognised falls back to the default rather than
    // being passed through to av_opt_set as-is.
    private static string NvencPreset(ReplayBufferConfig config) => config.EncoderPreset?.ToLowerInvariant() switch
    {
        "p1" or "p2" or "p3" or "p4" or "p5" => config.EncoderPreset!.ToLowerInvariant(),
        _ => "p4"
    };

    private static unsafe void ApplyLowLatencyEncoderOptions(AVCodecContext* codecContext, string candidateName, ReplayBufferConfig config)
    {
        void TrySet(string name, string value)
        {
            var result = ffmpeg.av_opt_set(codecContext->priv_data, name, value, 0);
            if (result < 0)
            {
                AppLog.Info($"Native encoder probe: {candidateName} option {name}={value} not supported (error {result}), skipping.");
            }
        }

        // Every saved clip is cut out of the ring buffer starting at a packet
        // flagged AV_PKT_FLAG_KEY. That flag means "I-frame", which is NOT the
        // same as IDR: without forced-idr, NVENC emits plain I-frames at GOP
        // boundaries, and frames after one may still reference frames before
        // it. A decoder starting cold at that cut therefore has references it
        // never saw. Software decoding conceals the damage, but a hardware
        // decoder shows it as blocky macroblock corruption - which is exactly
        // the symptom that forced editor playback onto software decode
        // (avcodec-hw=none in PlaybackSession) in the first place, and why the
        // same clips looked fine in standalone VLC only after it fell back to
        // software too. Making every keyframe a true IDR means any cut point
        // is a self-contained stream start.
        switch (candidateName)
        {
            case "h264_nvenc":
                // User-selectable now (Settings -> Encoder), defaulting to p4.
                // p1 was the old hardcoded value, chosen back when encode ran
                // inline on the capture thread and gave GPU rendering priority
                // during VSync-off/VRR bursts; encode has since moved to its own
                // thread (see EncodeLoop) and real logs measure it at ~0.5ms
                // against a 16.67ms frame budget, so there is margin to spend on
                // motion quality. The historical warning in the comment above
                // still applies at the top of the range: watch
                // droppedFrames/queueDepth under sustained heavy GPU load.
                TrySet("preset", NvencPreset(config));
                // Main is NVENC's default profile and disables the 8x8
                // transform - a free efficiency loss on precisely the detailed
                // content that was coming out soft.
                TrySet("profile", "high");
                // Spend bits by local complexity rather than uniformly, so flat
                // regions stop stealing budget from detailed ones.
                TrySet("spatial-aq", "1");
                // "hq", NOT "ll". Low-latency tuning pins NVENC's VBV buffer to
                // roughly a single frame's worth of bits, which hard-caps EVERY
                // frame at bitrate/fps (~34KB at 1080p60) no matter how much
                // motion it actually contains - a fast-panning gameplay frame
                // needs several times that, so it gets crushed to fit and comes
                // out soft and blocky while the file's AVERAGE bitrate still
                // reads a healthy ~17Mbps. That constraint buys nothing here:
                // this is a ring buffer being written to memory, not a live
                // stream with a latency budget. "hq" lets the encoder spend bits
                // where the content actually needs them, at the same average
                // bitrate and so the same file size and ring-buffer footprint.
                TrySet("tune", "hq");
                // Constant quality: cq drives the actual bit spend and
                // bit_rate/rc_max_rate (set on the context) are only an average
                // target and a derived hard ceiling. Constant bitrate: the rate
                // itself is the constraint, so no cq at all.
                if (IsConstantBitrate(config))
                {
                    TrySet("rc", "cbr");
                }
                else
                {
                    TrySet("rc", "vbr");
                    TrySet("cq", ConstantQualityTarget(config).ToString());
                }
                TrySet("forced-idr", "1");
                break;
            // AMF/QSV keep their existing usage/preset strings: there's no AMD
            // or Intel hardware here to confirm a change doesn't cost frames,
            // and these paths are untested. They still pick up the context-level
            // improvements (profile, colour tagging, VBV, rc_max_rate) for free.
            case "h264_amf":
                TrySet("usage", "ultralowlatency");
                TrySet("quality", "speed");
                // AMF's equivalent knob is spelled with an underscore, not a
                // dash like NVENC's - this was silently a no-op under the
                // wrong name (TrySet just logs and moves on for an unknown
                // option), so AMD captures weren't actually getting true IDR
                // cut points despite the setting looking present here.
                TrySet("forced_idr", "1");
                break;
            case "h264_qsv":
                TrySet("preset", "veryfast");
                TrySet("forced_idr", "1");
                // QSV's default queues up to 4 frames deep before an encoded
                // packet comes back out - same latency concern as NVENC's
                // "ll" tune above, just a different knob for a different SDK.
                TrySet("async_depth", "1");
                break;
            case "libx264":
                TrySet("preset", "ultrafast");
                TrySet("tune", "zerolatency");
                // x264's own constant-quality knob, so the CPU fallback tracks
                // the same user setting the hardware encoders do. In
                // constant-bitrate mode it's left off and bit_rate governs.
                if (!IsConstantBitrate(config)) TrySet("crf", ConstantQualityTarget(config).ToString());
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
            // The scalers (CreateScaler/CreateGpuScaler) now convert the
            // full-range capture into full-range NV12 - without also
            // signaling that in the bitstream's VUI, a decoder (including
            // this app's own editor) has no way to know and falls back to
            // assuming limited range, crushing blacks/whites right back in
            // on playback despite the pixels themselves being correct.
            codecContext->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            // Both scalers convert with BT.709 coefficients, but these tags
            // were never written, leaving files marked "unknown" - which every
            // player then resolves to BT.709 for HD anyway. That accidentally
            // worked only because the conversion side has now been moved to 709
            // to match; tagging it explicitly is what actually makes the two
            // ends agree instead of relying on a coincidence of defaults.
            codecContext->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            codecContext->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            codecContext->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            // Constant bitrate means the configured rate IS the target, so
            // bit_rate and the ceiling are the same number. Constant quality
            // leaves the resolution/fps-derived estimate as the nominal average
            // and lets a derived ceiling bound how far a burst may exceed it.
            var maxBitrate = MaxBitrate(config);
            codecContext->bit_rate = IsConstantBitrate(config) ? maxBitrate : CaptureBitrate(config);
            // A real VBV to spend against, rather than leaving it unset and
            // letting the encoder fall back to an effectively per-frame budget.
            // One second of buffer lets a burst of hard-to-compress motion
            // borrow bits from the quiet stretch around it, which is the whole
            // mechanism that keeps fast gameplay from going soft. rc_max_rate
            // is the hard ceiling from the user's quality setting - with
            // constant-quality rate control it's this, not bit_rate, that bounds
            // how large a clip (and the in-memory ring buffer) can actually get.
            codecContext->rc_buffer_size = (int)codecContext->bit_rate;
            codecContext->rc_max_rate = maxBitrate;
            codecContext->gop_size = 240;
            codecContext->max_b_frames = 0;
            codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            ApplyLowLatencyEncoderOptions(codecContext, candidateName, config);

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

        throw new InvalidOperationException("No usable H.264 encoder found (tried NVENC, AMD AMF, Intel QSV, software libx264).");
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
            var hr = Marshal.QueryInterface(device.NativePointer, in multithreadIid, out multithreadPtr);
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointStruct point);

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

    // Data comes from ArrayPool and is therefore usually LONGER than the packet
    // it holds - Length, not Data.Length, is the packet. Every consumer must
    // respect that.
    private readonly record struct RingPacket(byte[] Data, int Length, long PtsMs, bool IsKeyframe, DateTime WallClockUtc);

    private readonly record struct PauseEvent(DateTime WallClockUtc, bool IsPaused);
}
