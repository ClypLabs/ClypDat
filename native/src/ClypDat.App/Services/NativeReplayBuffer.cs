using ClypDat.Capture.Abstractions;
using FFmpeg.AutoGen;
using System.Collections.Concurrent;
using System.Globalization;
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
// DXGI Desktop Duplication is the primary source for game and desktop capture.
// It hands this loop an ID3D11Texture2D which remains on the capture device for
// crop/scale/encode. Windows.Graphics.Capture takes over only if DXGI fails to
// initialize, cannot recover, or repeatedly delivers stale game frames while
// the encoder is healthy.
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
// An uncapped/tearing game can present outside DWM's desktop-composition
// cadence. DXGI may then acquire frequently but produce stale cropped content.
// The live cadence policy detects that mismatch and switches game windows to
// WGC, which captures their content directly.
//
// Falls back to capturing the primary monitor (no crop, no occlusion pausing)
// when no game window is detected. Hardware encoding is preferred; libx264 is
// the last-resort live fallback after sustained hardware congestion. Audio reuses
// AudioCapturePipeline - the same Game/Chat/Microphone routing, WASAPI capture, and mux
// logic WindowsReplayBuffer uses, via its own independent instance.
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class NativeReplayBuffer : IReplayBuffer, IReplayCaptureDiagnostics, IAdaptiveCaptureFrameRate, IDetectorFrameSource
{
    internal static bool CanUseDirectVideoProcessorInput(bool directBltAvailable, bool requiresCopyBeforeProcessing) =>
        directBltAvailable && !requiresCopyBeforeProcessing;

    internal static bool IsSupportedDetectorAspectRatio(int width, int height) =>
        width > 0 && height > 0 && Math.Abs((double)width / height - 16.0 / 9.0) <= 0.01;

    internal static TimeSpan NextDxgiAcquireDeadline(TimeSpan scheduled, TimeSpan completed, int frameRate)
    {
        var cadence = Math.Clamp(frameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate);
        var next = scheduled + TimeSpan.FromSeconds(1.0 / cadence);
        return next < completed ? completed : next;
    }

    private static readonly int[] ReplayFrameRateLadder = [120, 90, 60, 30];
    // How long teardown waits for the pacing thread before giving up and leaking its
    // native resources instead of freeing them underneath it. Generous because
    // RunPacingTick contends on _bufferLock, does filesystem deletes, and can encode a
    // catch-up burst in one tick.
    private static readonly TimeSpan PacingThreadStopTimeout = TimeSpan.FromSeconds(10);


    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly string _bufferFolder;
    private readonly AudioCapturePipeline _audio;
    private readonly object _bufferLock = new();
    // One clip encode at a time. Each save runs ffmpeg to build its audio
    // tracks and mux the result - three or four processes, ~1.5s - and a burst
    // of saves ran all of them at once. Four saves inside five seconds put the
    // encode queue at 30/30 with 18 dropped frames a window, cut capture from
    // 120 frames per 2s to 18, and slowed the GAME's own presents to 28-54ms:
    // the clips saved during it read 41-49fps, while the two seconds after the
    // storm passed logged a clean 120/120 with nothing dropped or skipped.
    //
    // The window is still borrowed the moment the hotkey is pressed, so a
    // queued save still covers the moment the user asked for - only the
    // expensive part waits.
    private static readonly SemaphoreSlim SaveEncodeGate = new(1, 1);
    // Saves currently running, published on the health record so the tuner can
    // discount overload a save caused - see ReplayCaptureHealth.SaveInProgress.
    private int _savesInFlight;
    private readonly List<RingPacket> _packets = new();
    private readonly PacketPayloadPool _packetPayloads = new();
    // Running total of _packets' payload bytes, maintained on add/trim under
    // _bufferLock. The diagnostic that reports this used to sum the whole list
    // every 2 seconds - a LINQ walk over up to 14000 entries while holding the
    // one lock the encode thread needs for every packet it produces.
    private long _ringBufferBytes;
    private long _ringBufferCapacityBytes;
    // Recording-paused transitions (see class summary) - trimmed alongside
    // _packets so this never grows unbounded across a long session.
    private readonly List<PauseEvent> _pauseEvents = new();
    private readonly List<(DateTime StartUtc, DateTime? EndUtc)> _recoveryOutages = new();
    private DateTime? _recoveryCleanSinceUtc;
    private int _recoveryHealthyWindows;

    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private Task? _backgroundFinalize;
    // Guards StartAsync's orphan-WAV sweep across the app: a background
    // finalize still owns capture WAVs after its session stopped, and a new
    // session starting meanwhile must not sweep them out from under it.
    private static int _activeBackgroundFinalizes;
    private volatile bool _sessionActive;
    private AVRational _timeBase = new() { num = 1, den = 1_000_000 };
    // One entry per encoder generation, not one per session. A live encoder
    // failover (see CaptureLoop's overload swap, and the rebind after a device
    // rebuild) replaces the encoder without clearing the ring, and AV_CODEC_
    // FLAG_GLOBAL_HEADER means the packets carry no in-band SPS/PPS to recover
    // from. Muxing generation N's slices under generation 0's extradata is what
    // produced whole clips of flat grey with working audio. Indexed by
    // RingPacket.Generation; only ever appended to, and only under _bufferLock.
    private readonly List<EncoderGenerationInfo> _encoderGenerations = new();
    private int _encoderGeneration;
    // Codec family only (H.264 vs AV1), used to pick the full-session export
    // pass. Failover never crosses families, so this stays valid across a swap;
    // the per-generation codec id used for muxing lives in _encoderGenerations.
    private AVCodecID _videoCodecId = AVCodecID.AV_CODEC_ID_H264;
    // Set by the encode thread when it binds a replacement encoder, consumed by
    // the pacing thread on its next frame.
    private int _forceKeyframeRequested;
    private int _outputWidth;
    private int _outputHeight;
    // Ticks of the last moment genuinely new captured content was scaled into
    // the encoder's frame - the ring buffer alone can't answer "was this clip
    // real?", because a stalled source still produces a full ring of packets,
    // all of them the same padded frame. Written from CaptureLoop, read from
    // SaveReplayAsync on a different thread, so it goes through Volatile.
    private long _lastRealContentTicks;
    private volatile bool _lastSaveVideoWasFrozen;
    // Seconds of buffered history the last save had to leave out because the
    // encoder was replaced part-way through the window. Written under
    // _bufferLock in BorrowWindowUnderLock, read once the save returns.
    private double _lastSaveTrimmedBySeconds;

    public bool LastSaveVideoWasFrozen => _lastSaveVideoWasFrozen;
    public double LastSaveTrimmedBySeconds => Volatile.Read(ref _lastSaveTrimmedBySeconds);
    // Encode-thread diagnostics (see EncodeLoop) - written with Interlocked from
    // that thread, read/reset from CaptureLoop's own periodic diag line. Plain
    // instance fields are safe here since only one capture session (and so only
    // one encode thread) is ever active at a time.
    private long _encodeInputMicrosAccum;
    private long _encodeInputCountAccum;
    private long _encodeOutputMicrosAccum;
    private long _encodeOutputCountAccum;
    private long _packetCopyMicrosAccum;
    private long _packetCopyCountAccum;
    private long _ringInsertMicrosAccum;
    private long _ringInsertCountAccum;
    private long _encodeDroppedCount;
    // Diagnostics for the gap between what capture hands the encoder and what
    // reaches the ring. Clips arrive 52-260 frames short of a capture that logs
    // a dense 60fps with droppedFrames and padsSkipped at 0, and three attempts
    // at fixing the suspected cause have made things worse - so measure where
    // the frames actually go before touching the path again. Counters only,
    // no behaviour attached.
    // A save holds references into the ring instead of copying its bytes out,
    // so payload recycling has to wait for it - see BorrowWindowUnderLock.
    private int _borrowedWindowDepth;
    private readonly List<byte[]> _deferredPayloadReturns = new();
    private long _sendRefusedEagainCount;
    private long _sendFailedOtherCount;
    private long _packetsOutCount;
    private long _totalDroppedFrames;
    private int _peakQueueDepth;
    private int _pendingEncoderFrames;
    private int _peakPendingEncoderFrames;
    // Requests are applied by the pacing thread, the sole owner of the cadence
    // clock.  Keeping this separate from settings lets overload recovery lower
    // the active rate without rewriting the user's selected rate.
    private int _requestedFrameRate;
    private int _activeFrameRate;
    private int _configuredFrameRate;
    private int _frameRateProtectionEnabled;
    private int _frameRateProtectionActive;
    private DateTime? _lastDegradedUtc;
    private ReplayCaptureHealth _health = ReplayCaptureHealth.Unknown("Native");

    public NativeReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
        _bufferFolder = Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "native-replay-buffer");
        _audio = new AudioCapturePipeline(_bufferFolder);
    }

    public bool IsRecording => _sessionActive;

    public void RequestFrameRate(int frameRate)
    {
        if (!_sessionActive) return;
        var configured = Volatile.Read(ref _configuredFrameRate);
        if (configured <= 0) return;
        var requested = ClampReplayFrameRate(frameRate, configured);
        Interlocked.Exchange(ref _requestedFrameRate, requested);
        Interlocked.Exchange(ref _frameRateProtectionActive, requested < configured ? 1 : 0);
        AppLog.Info($"Native capture: requested active cadence {requested} FPS (selected {configured} FPS).");
    }

    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);
    public event EventHandler? RecordingStopped;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;
    public event EventHandler<DetectorFrameSnapshot>? DetectorFrameAvailable;

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

    private void UpdateRecoveryTimeline(bool unhealthy, bool paused, DateTime nowUtc)
    {
        lock (_bufferLock)
        {
            if (paused) { _recoveryHealthyWindows = 0; return; }
            if (unhealthy)
            {
                _recoveryHealthyWindows = 0;
                _recoveryCleanSinceUtc = null;
                if (_recoveryOutages.Count == 0 || _recoveryOutages[^1].EndUtc is not null)
                    _recoveryOutages.Add((nowUtc, null));
                return;
            }
            if (_recoveryOutages.Count == 0 || _recoveryOutages[^1].EndUtc is not null) return;
            if (++_recoveryHealthyWindows < 2) return;
            _recoveryOutages[^1] = (_recoveryOutages[^1].StartUtc, nowUtc);
            _recoveryCleanSinceUtc = nowUtc;
        }
    }

    private bool TryGetSafeSaveStart(DateTime requestedStartUtc, DateTime requestedEndUtc, out DateTime saveStartUtc)
    {
        lock (_bufferLock)
            return TryGetSaveStartAfterRecovery(_recoveryOutages, requestedStartUtc, requestedEndUtc, out saveStartUtc);
    }

    // A short, completed recovery should shorten a clip, not discard every
    // otherwise-good second in its requested window. An ongoing recovery has
    // no trustworthy tail yet, so it remains a failed save.
    internal static bool TryGetSaveStartAfterRecovery(
        IReadOnlyList<(DateTime StartUtc, DateTime? EndUtc)> recoveryOutages,
        DateTime requestedStartUtc,
        DateTime requestedEndUtc,
        out DateTime saveStartUtc)
    {
        saveStartUtc = requestedStartUtc;
        foreach (var outage in recoveryOutages)
        {
            var outageEndUtc = outage.EndUtc;
            if (outage.StartUtc >= requestedEndUtc || outageEndUtc is { } ended && ended <= requestedStartUtc) continue;
            if (outageEndUtc is null || outageEndUtc >= requestedEndUtc) return false;
            if (outageEndUtc > saveStartUtc) saveStartUtc = outageEndUtc.Value;
        }

        return saveStartUtc < requestedEndUtc;
    }

    private static int ClampReplayFrameRate(int requested, int configured)
    {
        var ceiling = Math.Clamp(configured, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate);
        return ReplayFrameRateLadder.FirstOrDefault(rate => rate <= ceiling && requested >= rate, ReplayFrameTimingPolicy.MinimumFrameRate);
    }

    private static int NextLowerReplayFrameRate(int current)
        => ReplayFrameRateLadder.FirstOrDefault(rate => rate < current, current);

    private static int NextHigherReplayFrameRate(int current, int configured)
    {
        for (var index = ReplayFrameRateLadder.Length - 1; index >= 0; index--)
        {
            var candidate = ReplayFrameRateLadder[index];
            if (candidate > current && candidate <= configured) return candidate;
        }
        return current;
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
        var configuredFrameRate = Math.Clamp(config.FrameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate);
        Volatile.Write(ref _configuredFrameRate, configuredFrameRate);
        Volatile.Write(ref _requestedFrameRate, configuredFrameRate);
        Volatile.Write(ref _activeFrameRate, configuredFrameRate);
        Volatile.Write(ref _frameRateProtectionEnabled, config.AdaptiveFrameRateProtectionEnabled ? 1 : 0);
        Volatile.Write(ref _frameRateProtectionActive, 0);
        Duration = TimeSpan.FromSeconds(Math.Clamp(config.DurationSeconds, 30, 1200));

        // Captured per encoder generation from that encoder's own SPS/PPS (see
        // DrainToRingBuffer) - without resetting here, a resolution change +
        // Restart Buffer opens a new encoder at the new size but keeps muxing
        // clips with the PREVIOUS session's stale extradata, which still
        // declares the old resolution. The container's declared size then
        // doesn't match the actual encoded frame data, producing exactly the
        // stride-mismatch smearing/corruption reported after a resolution
        // change.
        lock (_bufferLock)
        {
            _encoderGenerations.Clear();
            _encoderGeneration = 0;
        }
        Interlocked.Exchange(ref _totalDroppedFrames, 0);
        Volatile.Write(ref _peakQueueDepth, 0);
        Volatile.Write(ref _pendingEncoderFrames, 0);
        Volatile.Write(ref _peakPendingEncoderFrames, 0);
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
        _packetPayloads.Activate();
        _sessionActive = true;
        SetHealth(new ReplayCaptureHealth("Native", "Native capture", ReplayCaptureState.Starting,
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
            _ringBufferCapacityBytes = 0;
            _pauseEvents.Clear();
            _recoveryOutages.Clear();
            _recoveryCleanSinceUtc = null;
            _recoveryHealthyWindows = 0;
        }
        _packetPayloads.Deactivate();
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null, string? gameDisplayNameOverride = null)
    {
        if (!_sessionActive) throw new InvalidOperationException("Replay buffer is not recording.");
        if (_health.StartupPhase is ReplayCaptureStartupPhase.WaitingForForeground or ReplayCaptureStartupPhase.OpeningEncoder or ReplayCaptureStartupPhase.Fallback)
            throw new InvalidOperationException("Replay encoder is still opening.");

        var requestedStartUtc = clipWindow?.StartUtc ?? MonotonicClock.UtcNow - Duration;
        var requestedEndUtc = clipWindow?.EndUtc ?? MonotonicClock.UtcNow;
        if (requestedEndUtc <= requestedStartUtc)
        {
            throw new InvalidOperationException("The requested replay window is empty.");
        }
        if (!TryGetSafeSaveStart(requestedStartUtc, requestedEndUtc, out var safeSaveStartUtc))
            throw new InvalidOperationException("Replay capture is still recovering. Try again after recording resumes.");
        var recoveryTrimmed = safeSaveStartUtc > requestedStartUtc;
        if (recoveryTrimmed)
        {
            AppLog.Info($"Native replay save: recovery overlapped requested window; trimming {(safeSaveStartUtc - requestedStartUtc).TotalSeconds:0.0}s of unstable history.");
            requestedStartUtc = safeSaveStartUtc;
        }

        // Selected under the lock, payloads borrowed rather than copied - see
        // BorrowWindowUnderLock. Released in the finally at the end of this
        // method, which is what lets the ring recycle again.
        var window = BorrowWindowUnderLock(requestedStartUtc, requestedEndUtc, startAtOrAfterRequestedUtc: recoveryTrimmed);
        var saveGateHeld = false;
        // Hoisted so the finally can hand the borrowed-window release to it. The remux
        // reads the borrowed payload arrays directly, and the release returns them to
        // the pool for DrainToRingBuffer to immediately re-rent and overwrite - so
        // releasing while the remux is still copying splices frames from a later moment
        // of the recording into the saved clip.
        Task<long>? remuxTask = null;
        Interlocked.Increment(ref _savesInFlight);
        try
        {
        await SaveEncodeGate.WaitAsync(cancellationToken);
        saveGateHeld = true;

        var config = _configProvider();
        var gameDisplayName = string.IsNullOrWhiteSpace(gameDisplayNameOverride) ? config.GameDisplayName : gameDisplayNameOverride;
        var clipName = string.IsNullOrWhiteSpace(titleOverride) ? gameDisplayName : titleOverride;
        var gameFolder = Path.Combine(outputFolder, ClipFileNaming.BuildBaseName(gameDisplayName));
        Directory.CreateDirectory(gameFolder);
        var outputPath = ClipFileNaming.BuildUniquePath(gameFolder, ClipFileNaming.BuildFileName(clipName, DateTime.Now, "mp4", config.ClipFileNameScheme, config.CustomClipFileNameTemplate, gameDisplayName));

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
            // Stage timings, because "the save takes ages" is not something a
            // log full of per-stage silence can answer. A real report put 50.9s
            // between the hotkey and the saved toast with only the audio
            // snapshot lines to show for it.
            var saveTimer = System.Diagnostics.Stopwatch.StartNew();
            // Started, not awaited. The audio pipeline below needs nothing from
            // the remux (it works off `window`'s timestamps and its own capture
            // WAVs) and the remux needs nothing from it, but they used to run
            // strictly one after the other anyway - on a laptop that meant ~9s
            // of disk-bound remux followed by ~30s of CPU-bound ffmpeg, when
            // the two barely compete for the same resource. Awaited together
            // below, before the mux that is the first thing to need both.
            remuxTask = Task.Run(() =>
            {
                var stageTimer = System.Diagnostics.Stopwatch.StartNew();
                var previousPriority = Thread.CurrentThread.Priority;
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                try { RemuxWindowToMp4(window, tempVideoPath, requestedEndUtc, ReplayFrameTimingPolicy.IsVariable(config.FrameRateMode)); }
                finally { Thread.CurrentThread.Priority = previousPriority; }
                return stageTimer.ElapsedMilliseconds;
            }, cancellationToken);

            // The ring buffer already remuxes exactly the desired window starting at a
            // real keyframe - no offset/trim needed here the way WindowsReplayBuffer's
            // keyframe-seek fallback requires.
            var windowStartUtc = window[0].WallClockUtc;
            var windowDurationSeconds = Math.Max(1, (requestedEndUtc - windowStartUtc).TotalSeconds);

            // Diagnostic only (see audio-desync investigation) - video's own
            // internal duration comes from Stopwatch-based PTS (monotonic,
            // high precision), while the audio segment above is sized from
            // wall-clock (DateTime.UtcNow) deltas between the same two
            // packets. If these disagree by more than a few ms, the audio
            // track gets built to a different total length than the video
            // actually has, which wouldn't just be a start offset - it'd get
            // worse toward the end of the clip.
            var finalPacketDurationMicroseconds = ReplayFrameTimingPolicy.IsVariable(config.FrameRateMode)
                ? Math.Max(1, (long)Math.Round((requestedEndUtc - window[^1].WallClockUtc).TotalMilliseconds * 1_000))
                : window.Length > 1
                    ? Math.Max(1, window[^1].PtsMs - window[^2].PtsMs)
                    : Math.Max(1, 1_000_000L / Math.Clamp(config.FrameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate));
            var videoDurationSeconds = (window[^1].PtsMs - window[0].PtsMs + finalPacketDurationMicroseconds) / 1_000_000.0;
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
            //
            // The threshold used to be a full second, which is roughly 20x the
            // point at which a human hears audio and picture come apart. On a
            // machine whose encoder is struggling the shortfall lands squarely
            // in the gap that left open: measured saves on an Iris Xe laptop
            // came in at 323ms and 486ms of a ~60s clip, both waved through
            // uncorrected. The audio then plays a window that covers more real
            // time than the video does, so the two drift steadily further
            // apart toward the end of the clip - loudest on game audio, where
            // gunshots and footsteps make a third of a second obvious in a way
            // voice chat does not. 50ms is about where a mismatch stops being
            // audible; below it the correction is not worth the frames.
            const double MaxAudioVideoDeltaSeconds = 0.05;
            if (windowDurationSeconds - videoDurationSeconds > MaxAudioVideoDeltaSeconds)
            {
                var message = $"Native replay: video came up short ({videoDurationSeconds:0.000}s of {windowDurationSeconds:0.000}s requested) - trimming audio to match.";
                // A second-plus shortfall is a capture stall and worth saying
                // out loud; the sub-second ones are routine on a loaded
                // machine and would only spam the log.
                if (windowDurationSeconds - videoDurationSeconds > 1.0) AppLog.Info(message);
                else AppLog.Debug(message);
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
            var chunkStartUtc = windowStartUtc;
            var remainingSeconds = windowDurationSeconds;
            while (remainingSeconds > 0)
            {
                var chunkSeconds = Math.Min(SegmentChunkSeconds, remainingSeconds);
                // Absorb a runt tail rather than emitting a segment for it. A
                // 60s replay buffer measures a hair over 60s of wall clock, so
                // the old loop produced a full segment plus a ~1.3s one - and
                // every segment costs an ffmpeg process PER TRACK, plus a
                // concat pass per track that a single segment does not need at
                // all. That turned the common 3-process save into 9, on the
                // machines least able to afford it. Drift over 61s is no worse
                // than over 60, which is why the 60s figure was approximate to
                // begin with.
                if (remainingSeconds - chunkSeconds < SegmentChunkSeconds / 2)
                {
                    chunkSeconds = remainingSeconds;
                }

                segmentWindows.Add((chunkStartUtc, chunkSeconds));
                chunkStartUtc += TimeSpan.FromSeconds(chunkSeconds);
                remainingSeconds -= chunkSeconds;
            }

            WritePausedRangesSidecar(config.LibraryFolder, outputPath, ComputePausedRangesSeconds(GetOrderedPauseEvents(), windowStartUtc, windowStartUtc + TimeSpan.FromSeconds(windowDurationSeconds)));

            var tracksStartMs = saveTimer.ElapsedMilliseconds;
            List<(string Label, string Path)> tracks;
            try
            {
                tracks = await _audio.BuildAlignedTracksAsync(segmentWindows, config, snapshots, cancellationToken);
            }
            catch
            {
                // Let the remux finish before the finally deletes the file it
                // is still writing, and observe its own failure if it had one.
                try { await remuxTask; } catch { /* the audio failure is the one worth reporting */ }
                throw;
            }

            var tracksMs = saveTimer.ElapsedMilliseconds - tracksStartMs;
            // The mux is the first stage that needs the video file, so this is
            // where the concurrent remux gets collected. Usually already done.
            var remuxMs = await remuxTask;
            var remuxWaitMs = saveTimer.ElapsedMilliseconds - tracksStartMs - tracksMs;

            var muxStartMs = saveTimer.ElapsedMilliseconds;
            var muxArgs = new List<string> { "-y", "-i", tempVideoPath };
            foreach (var track in tracks) muxArgs.AddRange(new[] { "-i", track.Path });
            muxArgs.AddRange(new[] { "-map", "0:v" });
            for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { "-map", $"{i + 1}:a" });
            muxArgs.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "192k" });
            for (var i = 0; i < tracks.Count; i++)
            {
                muxArgs.AddRange(new[] { $"-metadata:s:a:{i}", $"handler_name={tracks[i].Label}" });
                muxArgs.AddRange(new[] { $"-metadata:s:a:{i}", $"title={tracks[i].Label}" });
            }
            muxArgs.AddRange(new[] { "-movflags", "+faststart" });
            muxArgs.AddRange(new[] { "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("Native")}", outputPath });
            var result = await AudioCapturePipeline.RunProcessAsync("ffmpeg", muxArgs, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg mux failed." : result.Error);
            }

            AppLog.Info($"Native replay save timings: totalMs={saveTimer.ElapsedMilliseconds}, remuxMs={remuxMs} (waited {remuxWaitMs}), audioTracksMs={tracksMs}, muxMs={saveTimer.ElapsedMilliseconds - muxStartMs}, tracks={tracks.Count}, segments={segmentWindows.Count}, packets={window.Length}.");
        }
        finally
        {
            AudioCapturePipeline.TryDelete(tempVideoPath);
            foreach (var snapshot in snapshots) AudioCapturePipeline.TryDelete(snapshot);
        }

        AppLog.Info($"Native replay saved: path={outputPath}, packets={window.Length}.");
        return outputPath;
        }
        finally
        {
            if (saveGateHeld) SaveEncodeGate.Release();
            Interlocked.Decrement(ref _savesInFlight);

            // The happy path awaits remuxTask before reaching here, but anything that
            // throws between starting it and that await - the segment-chunking loop or
            // the sidecar write - lands here with the remux still running. A finally
            // cannot await, so defer the release onto the task instead of blocking a
            // possibly UI-bound continuation.
            var pendingRemux = remuxTask;
            if (pendingRemux is not null && !pendingRemux.IsCompleted)
            {
                _ = pendingRemux.ContinueWith(_ => ReleaseBorrowedWindow(), TaskScheduler.Default);
            }
            else
            {
                ReleaseBorrowedWindow();
            }
        }
    }

    private int _capturePaused;
    public void SetCapturePaused(bool paused) => Volatile.Write(ref _capturePaused, paused ? 1 : 0);

    public void Dispose()
    {
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _packetPayloads.Deactivate();
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
        DesktopDuplicationFrameSource? dxgiCapture = null;
        WindowGraphicsCaptureSource? wgcCapture = null;
        IGameFrameSource? activeGameFrameSource = null;
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
        // Detector readback is independent from encoder readback. Its three
        // slots are sampled at 2 FPS and always mapped with DoNotWait, so a
        // busy GPU drops a detector frame instead of delaying capture.
        ID3D11Texture2D[]? detectorStagingRing = null;
        var detectorStagingIndex = 0;
        var detectorRingWritten = 0;
        var lastDetectorSample = TimeSpan.MinValue;
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
        // Set when the scale/convert Blt already ran at present time straight
        // off an owned source texture, so the encode tick only has to read
        // nv12Output back rather than produce it. See directBltAvailable.
        var nv12Ready = false;
        // The crop copy above exists purely to outlive the duplication frame.
        // It is also a full-resolution BGRA copy - at 4K that is 33MB read plus
        // 33MB written, and the gate below permits it up to TWICE per encode
        // interval, so a busy screen pays ~8GB/s for it. Measured: this app's
        // 3D-engine share sits at ~6.5% on a static desktop and climbs past 25%
        // when the screen is changing, which is the shape of exactly this copy
        // and nothing else in the pipeline (the Blt and the readback are both
        // pinned to the encode rate).
        //
        // It is avoidable for sources whose texture is already owned by this
        // processing device. The Video Processor can read those directly with
        // a source rect. It is deliberately not avoidable for DXGI's transport
        // ring: its CPU lease must end immediately behind this copy, before the
        // downstream scale/convert Blt.
        //
        // Falls back to the copy for the rest of the session if a driver will
        // not hand out an input view for a duplication texture.
        // CLYPDAT_DISABLE_DIRECT_BLT=1 forces the staging-copy path for every
        // source. DXGI transport leases always take that path regardless: their
        // producer slots must be released immediately after one bounded copy.
        var directBltAvailable = Environment.GetEnvironmentVariable("CLYPDAT_DISABLE_DIRECT_BLT") != "1";
        // Input views are per-texture, and capture sources rotate a small pool
        // of them, so these are cached by native pointer rather than
        // rebuilt per frame. Disposed with the rest of the D3D state.
        var desktopInputViews = new Dictionary<nint, ID3D11VideoProcessorInputView>();
        // Cursor position for the GPU path, already converted into OUTPUT-resolution
        // pixels at crop time. The crop block is the only place the crop origin and
        // capture size are in scope, but the cursor has to be drawn after the scaled
        // readback (drawing it before would mean the video processor resamples the
        // arrow along with the frame). int.MinValue means "no cursor this frame".
        var cursorOutputX = int.MinValue;
        var cursorOutputY = int.MinValue;
        ID3D11Texture2D? cursorTexture = null;
        ID3D11VideoProcessorInputView? cursorInputView = null;
        var gpuCursorAvailable = false;
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
        // Zero-copy encode state (see TryCreateD3D11EncodeFrames). All three
        // stay 0/null on the system-memory path, which is what every check
        // below tests for.
        nint hwDeviceRef = 0;
        nint hwFramesRef = 0;
        // The most recently filled pool frame, kept referenced so a padding
        // tick (nothing new presented) can re-send the same texture instead of
        // burning a pool slot on a byte-identical copy.
        AVFrame* lastHardwareFrame = null;
        // Vortice wrappers over the pool's texture pointers. ffmpeg hands back
        // a raw ID3D11Texture2D*; wrapping it fresh per frame would allocate 60
        // times a second, and disposing a wrapper would Release a reference the
        // pool owns - so these are cached for the session and never disposed.
        var hardwarePoolTextures = new Dictionary<nint, ID3D11Texture2D>();
        // AMF may retain a system-memory surface after avcodec_send_frame
        // returns. Keep one reference to the last submitted software buffer so
        // the next real capture forces FFmpeg copy-on-write instead of AMF
        // seeing changed pixels through the same host pointer.
        AVFrame* amfSoftwareFrameGuard = null;
        var requiresDistinctAmfSoftwareFrame = false;
        // Encoders retired by a mid-session swap (see EncodeJob). Freed only
        // after the encode thread has joined - it may still be inside one.
        var retiredCodecContexts = new List<nint>();
        var swapCompletionEvents = new List<ManualResetEventSlim>();
        var fullSessionTempVideoPath = string.Empty;
        var fullSessionFinalOutputPath = string.Empty;
        var fullSessionStartUtc = MonotonicClock.UtcNow;
        // Real wall-clock twin of fullSessionStartUtc, only for the sidecar's
        // user-facing CreatedAt - all alignment math stays on MonotonicClock.
        var fullSessionStartWallUtc = DateTime.UtcNow;
        var fullSessionGameDisplayName = string.Empty;
        var timerResolutionRaised = TimeBeginPeriod(1) == 0;
        // The capture loop has one frame interval to acquire, scale and hand
        // off each frame, and a missed one is a permanent hole in the clip.
        // MMCSS is what keeps this thread scheduled while a game owns every
        // core - see MmcssScope.Capture.
        using var captureMmcss = MmcssScope.Capture("native capture loop");
        var gpuLock = new object();
        // Decides in the finally whether the native frees are safe to run. Starts true
        // so that a failure before the pacing thread ever starts still tears down
        // normally; it is cleared the moment that thread is running, and set again only
        // by a successful join.
        var pacingThreadStopped = true;
        // Held so the finally can join it on EVERY exit path. The join after the capture
        // loop only runs on a normal exit; a cancellation or a device error throws from
        // inside the loop and jumps straight to the finally.
        Thread? pacingThread = null;

        try
        {
            var config = _configProvider();
            // Before any D3D work: a game that owns the GPU otherwise outranks
            // this process's own submissions, which is what turns an 8ms encode
            // into a 50ms one under load. Device priority is applied by worker.
            device = CreateD3D11Device(out var processingGpuPriority);
            var targetHandle = ResolveTargetWindow(config);
            var isMonitorMode = targetHandle == 0;
            // Which output the duplication below is actually bound to - see the
            // once-a-second target recheck in the loop for why the window handle
            // alone is the wrong thing to compare against.
            var targetMonitor = ResolveTargetMonitor(targetHandle, config);
            Vortice.RawRect desktopBounds;
            try
            {
                // Keep duplication and video processing on one ordered device.
                // The acquired surface can feed the Video Processor directly;
                // ReleaseFrame follows the queued Blt, so there is no full-frame
                // transport copy and no cross-device release-fence backlog.
                duplication = CreateDuplicationFor(device, targetHandle, config, out desktopBounds);
                AppLog.Info($"Native capture: using DXGI Desktop Duplication for {(isMonitorMode ? "desktop" : $"game window 0x{targetHandle:X}")}.");
            }
            catch (Exception error) when (!isMonitorMode)
            {
                AppLog.Error("Native capture: DXGI initialization failed; using bounded WGC recovery source.", error);
                wgcCapture = WindowGraphicsCaptureSource.Create(device, gpuLock, targetHandle, config.CaptureCursor, config.FrameRate);
                activeGameFrameSource = wgcCapture;
                var size = wgcCapture.ContentSize;
                desktopBounds = new Vortice.RawRect(0, 0, size.Width, size.Height);
            }

            var (captureWidth, captureHeight) = wgcCapture is not null
                ? wgcCapture.ContentSize
                : isMonitorMode
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
                detectorStagingRing =
                [
                    CreateNv12StagingTexture(device, outputWidth, outputHeight),
                    CreateNv12StagingTexture(device, outputWidth, outputHeight),
                    CreateNv12StagingTexture(device, outputWidth, outputHeight)
                ];
                (croppedTexture, inputView) = CreateGpuCropInputView(device, videoDevice, vpEnumerator, captureWidth, captureHeight);
                useGpuScale = true;
                AppLog.Info("Native capture: GPU downscale (D3D11 Video Processor) available, using it.");
            }
            catch (Exception error)
            {
                AppLog.Info($"Native capture: GPU downscale unavailable, falling back to CPU scale: {error.Message}");
                useGpuScale = false;
            }

            // Queue depth is decided before the encoder, because the D3D11 frame
            // Every queued hardware frame holds a VRAM surface. Replay capture
            // wants the newest game frame, not a second of stale work, so bound
            // the queue to roughly 125ms at every supported target rate.
            var encodeQueueCapacity = ReplayEncoderProfilePolicy.ReplayQueueCapacity(config.FrameRate);
            var pacingLatency = new ReplayLatencyHistogram();
            var processingLatency = new ReplayLatencyHistogram();
            var submissionLatency = new ReplayLatencyHistogram();
            var outputLatency = new ReplayLatencyHistogram();
            long pacingMissedFrames = 0;
            long encodeQueueReplacements = 0;
            var latestPacing = ReplayPacingPolicy.IsLatest(Environment.GetEnvironmentVariable("CLYPDAT_PACING_POLICY"));

            if (useGpuScale && config.CaptureCursor)
            {
                try
                {
                    (cursorTexture, cursorInputView) = CreateGpuCursorOverlay(device, videoDevice!, vpEnumerator!);
                    gpuCursorAvailable = true;
                }
                catch (Exception error)
                {
                    AppLog.Info($"Native capture: GPU cursor overlay unavailable; using system-memory cursor path ({error.Message}).");
                }
            }

            // Keep the scaler and FFmpeg encoder input on the same D3D11 device
            // so NVIDIA can consume the capture surfaces without a readback.
            if (useGpuScale && (!config.CaptureCursor || gpuCursorAvailable))
            {
                (hwDeviceRef, hwFramesRef) = TryCreateD3D11EncodeFrames(
                    device, outputWidth, outputHeight, ReplayEncoderProfilePolicy.D3D11FixedPoolSize(config.FrameRate, HardwareFramePoolHeadroom));
            }

            AVRational codecTimeBase;
            string encoderName;
            bool hardwareFramesActive;
            codecContext = CreateEncoder(config, outputWidth, outputHeight, hwFramesRef, device, out codecTimeBase, out encoderName, out hardwareFramesActive);
            _timeBase = codecTimeBase;
            _videoCodecId = codecContext->codec_id;
            if (!hardwareFramesActive) ReleaseHardwareFrames(ref hwDeviceRef, ref hwFramesRef);
            requiresDistinctAmfSoftwareFrame = !hardwareFramesActive && encoderName.Contains("amf", StringComparison.OrdinalIgnoreCase);

            swsContext = CreateScaler(captureWidth, captureHeight, outputWidth, outputHeight);

            frame = ffmpeg.av_frame_alloc();
            // Checked before the dereference below: av_frame_alloc returns NULL on OOM,
            // and every field assignment that follows would write through it.
            if (frame is null) throw new InvalidOperationException("av_frame_alloc failed.");
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            frame->width = outputWidth;
            frame->height = outputHeight;
            // The return was ignored, and the InitBlockUnaligned below writes through
            // frame->data[0] - which stays NULL when this fails.
            var frameBufferResult = ffmpeg.av_frame_get_buffer(frame, 32);
            if (frameBufferResult < 0 || frame->data[0] is null)
                throw new InvalidOperationException($"av_frame_get_buffer failed ({frameBufferResult}).");
            // av_frame_get_buffer leaves the buffer uninitialized - if the
            // target window starts occluded (recording begins before the game
            // has focus, common when starting the buffer from ClypDat's own
            // window), the very first frames get encoded straight from that
            // garbage NV12 data before any real capture ever lands in it,
            // which renders as solid green (Y/U/V all near zero decodes to
            // bright green in YUV->RGB). Fill it to black up front instead.
            FillFrameBlack(frame, outputHeight);

            encodeQueue = new BlockingCollection<EncodeJob>(boundedCapacity: encodeQueueCapacity);
            var encodeQueueGate = new object();

            var adapterDescription = DescribeAdapter(device);
            var videoCodec = encoderName.StartsWith("av1_", StringComparison.OrdinalIgnoreCase) ? "AV1" : "H.264";
            var captureMode = wgcCapture is null ? "Desktop Duplication" : "Windows Graphics Capture";
            AppLog.Info($"Native capture armed ({captureMode}): target={(targetHandle != 0 ? "window" : "primary monitor")}, source={captureWidth}x{captureHeight}, output={outputWidth}x{outputHeight}, encoder={encoderName}, encodePath={(hardwareFramesActive ? "D3D11 zero-copy" : "system-memory")}, cursorPath={(config.CaptureCursor ? "CPU" : "off")}, queue={encodeQueueCapacity} frames, adapter={adapterDescription}, profile={config.EncoderProfile}, frameTiming={ReplayFrameTimingPolicy.Normalize(config.FrameRateMode)}, configFrameRate={config.FrameRate}.");
            SetHealth(new ReplayCaptureHealth("Native", captureMode, ReplayCaptureState.Starting,
                config.FrameRate, 0, 0, 0, 0, 0, 0, encoderName, "Default adapter", string.Empty, DateTime.UtcNow)
            {
                AdapterDescription = adapterDescription,
                EncoderProfile = config.EncoderProfile,
                EncodeQueueCapacity = encodeQueueCapacity,
                FrameRateMode = ReplayFrameTimingPolicy.Normalize(config.FrameRateMode),
                ConfiguredFrameRate = config.FrameRate,
                EncoderInputPath = hardwareFramesActive ? "D3D11 zero-copy" : "System memory",
                FrameRateProtectionActive = false,
                StartupPhase = ReplayCaptureStartupPhase.WaitingForForeground,
                ProcessingGpuPriority = processingGpuPriority,
                AcquisitionGpuPriority = dxgiCapture?.AppliedGpuPriority
            });
            ready.TrySetResult();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Anchors packet->pts (a Stopwatch-based, accurate-to-the-real-
            // capture-moment value) back to a real wall-clock instant, so
            var lastForcedKeyframe = TimeSpan.Zero;
            var lastTargetRefresh = TimeSpan.Zero;
            var lastEncodedAt = TimeSpan.Zero;
            var activeFrameRate = Volatile.Read(ref _activeFrameRate);
            var targetFrameInterval = TimeSpan.FromSeconds(1.0 / activeFrameRate);
            var nextDxgiAcquireAt = TimeSpan.Zero;
            var variableFrameTiming = ReplayFrameTimingPolicy.IsVariable(config.FrameRateMode);
            // Counts encoded frames (including duplicate/padding ones) so
            // frame->pts can be assigned an IDEAL, constant-rate timestamp
            // (index * exact interval) rather than real elapsed time - see
            // the pacing gate below for why. RingPacket.WallClockUtc (used
            // only for audio alignment) is tracked completely separately via
            // a MonotonicClock.UtcNow captured at each actual encode, passed
            // straight into DrainToRingBuffer, so idealizing video's own
            // timeline can't drag audio sync off with it.
            // Accumulated rather than multiplying an index, keeping the CFR
            // clock exact without accumulating floating-point rounding error.
            var nextPtsMicroseconds = 0.0;
            long lastVariablePtsMicroseconds = -1;
            var idealFrameIntervalMicroseconds = 1_000_000.0 / activeFrameRate;
            var cropSamplingBudget = new PresentSamplingBudget(activeFrameRate);
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
            // Two frame intervals, capped at 33ms - it used to be half an interval
            // capped at 8ms.
            //
            // Windows bills this process for GPU engine time PER AcquireNextFrame
            // CALL, not per millisecond spent waiting in one. Measured on an idle
            // 4K desktop, where time inside the call was ~100% of wall clock at
            // every timeout tried, so only the call count moved:
            //
            //   timeout   calls/2s   3d
            //   8ms         280      22.7
            //   16ms        175      20.3
            //   33ms        112      12.9
            //   100ms        74       8.4
            //
            // Latency is unaffected while anything is actually happening, because
            // the timeout is a CEILING: AcquireNextFrame returns the moment a
            // frame arrives, so a source presenting at the target rate returns in
            // ~one interval no matter what this is set to. It only lengthens the
            // wait when the desktop is producing nothing - precisely when there is
            // no latency to lose.
            //
            // Not pushed to 100ms: this loop also drives the encode tick, so the
            // wait is how long that tick can be delayed, and at 100ms a single
            // iteration measured 93ms against a 16.7ms tick. 33ms keeps the worst
            // case near two intervals, which the catch-up path absorbs without
            // padding (padsSkipped stayed 0 in every run). Getting the rest of the
            // way needs acquisition moved off this thread entirely.
            // CLYPDAT_ACQUIRE_TIMEOUT_MS overrides the cap below, to test whether
            // acquisition and encode pacing sharing one loop is what gets this
            // process billed 20-25% of the GPU 3D engine on an idle desktop.
            //
            // The cap exists because this loop also drives the encode tick, which
            // has to fire every target interval - so the acquire wait can never
            // exceed half of one. On an idle desktop that means ~121 calls a
            // second against ~11 real frames, i.e. ~110 waits per second spent
            // INSIDE a DXGI call, which Windows bills as GPU engine time. The
            // Legacy/ScreenRecorderLib backend drives the same API from a thread
            // with no such constraint and measures 0.70% against this engine's
            // 21-27%.
            //
            // Raising it should trade billed GPU time for pacing accuracy: fewer,
            // longer waits, but an encode tick that wakes late. Watch padsSkipped
            // and avgPreAcquireMs alongside the GPU counter - if billed time falls
            // and pacing degrades, the coupling is confirmed and the real fix is
            // to decouple the two rather than to keep this override.
            var acquireTimeoutOverride = Environment.GetEnvironmentVariable("CLYPDAT_ACQUIRE_TIMEOUT_MS");
            var acquireTimeoutForcedMs = int.TryParse(acquireTimeoutOverride, out var parsedTimeout) && parsedTimeout is > 0 and <= 1000
                ? parsedTimeout
                : 0;
            // 200ms, and no longer tied to the frame interval at all. The encode
            // tick moved to its own thread (pacingThread below), so the reason the
            // wait had to stay under one interval is gone - and with it the ~56
            // calls a second this made on an idle desktop. An idle desktop now
            // costs about 5 calls a second. The env override stays for A/B-ing it.
            //
            // Not longer than this: the wait is also how long shutdown can take to
            // notice cancellation, and how long a target-window change or a
            // duplication recreate waits to be picked up.
            var acquireTimeoutMs = acquireTimeoutForcedMs > 0 ? (uint)acquireTimeoutForcedMs : 200u;
            if (acquireTimeoutForcedMs > 0) AppLog.Info($"Native capture: acquire timeout forced to {acquireTimeoutMs}ms.");
            var lastDiagLog = TimeSpan.Zero;
            var dxgiCadenceFallback = new DxgiCadenceFallbackPolicy();
            var sourceRecovery = new CaptureSourceRecoveryPolicy();
            var lastRingTrim = TimeSpan.Zero;
            var previousWgcTelemetry = default(WindowGraphicsCaptureTelemetry);
            var previousDxgiTelemetry = default(DesktopDuplicationTelemetry);
            var framesSeen = 0;
            var framesSeenSinceLog = 0;
            var pointerFramesSeenSinceLog = 0;
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
            // Raw per-present crop-copy counts. Deliberately NOT folded into
            // avgCopyMapMs: that average divides by frames ENCODED, which is
            // exactly the metric-reading mistake documented at the crop call
            // site (it understated the true per-present cost by the
            // source/target frame rate ratio). These two are the only numbers
            // that actually show whether the crop-copy rate limiter is working.
            var cropCopies = 0;
            var cropCopiesSkipped = 0;
            // Per-frame scratch, hoisted out of the hot path - see their use
            // sites. Allocated once per session rather than 60+ times a second.
            var bltStreams = new VideoProcessorStream[2];
            var swsSrcData = new byte*[1];
            var swsSrcStride = new int[1];
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
            var encoderHasProducedPacket = false;
            var consecutiveOverloadWindows = 0;
            var consecutiveTransportShortfallWindows = 0;
            var consecutiveWgcCongestionWindows = 0;
            var activeEncoderCandidate = default(ReplayEncoderCandidate);
            var attemptedEncoderCandidates = new HashSet<ReplayEncoderCandidate>();
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
            var freshContentSinceLastEncode = 0;
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
            // Backs off on repeated failure rather than staying at 2s forever.
            // A recovery that has failed dozens of times is not going to be
            // rescued by trying again sooner, and each device-rebuild attempt
            // is a real cost - a full D3D11 device creation plus an encoder
            // swap. Reset to the fast interval the moment frames come back.
            var baseRecoveryRetryInterval = TimeSpan.FromSeconds(2);
            var maxRecoveryRetryInterval = TimeSpan.FromSeconds(30);
            var recoveryRetryInterval = baseRecoveryRetryInterval;
            // Three failed recreates means the problem isn't the duplication.
            const int recoveryAttemptsBeforeDeviceRebuild = 3;

            // A dedicated thread, not a pool item: hitting a 16.7ms deadline is
            // the one thing this has to do, and a pool work item queues behind
            // whatever else the app is running (the same reasoning as
            // ProcessLoopbackWaveIn's capture thread). Its lateness is directly
            // visible in the output - as padsSkipped, and as a clip that reads
            // under its configured frame rate - so it also runs AboveNormal.
            pacingThread = new Thread(() =>
            {
                using var pacingMmcss = MmcssScope.Capture("native pacing thread");
                var nextTickAt = stopwatch.Elapsed;
                while (!token.IsCancellationRequested)
                {
                    // Sleep the bulk, yield the tail. Thread.Sleep resolution is
                    // whole milliseconds at best, so sleeping the whole way to the
                    // deadline overshoots it; stopping 1ms short and yielding
                    // through the remainder lands close without burning a core.
                    var remaining = nextTickAt - stopwatch.Elapsed;
                    while (remaining > TimeSpan.Zero)
                    {
                        if (remaining > TimeSpan.FromMilliseconds(2))
                            Thread.Sleep(remaining - TimeSpan.FromMilliseconds(1));
                        else
                            Thread.Yield();
                        remaining = nextTickAt - stopwatch.Elapsed;
                    }

                    try
                    {
                        var requestedRate = Volatile.Read(ref _requestedFrameRate);
                        if (requestedRate != activeFrameRate)
                        {
                            activeFrameRate = requestedRate;
                            targetFrameInterval = TimeSpan.FromSeconds(1.0 / activeFrameRate);
                            idealFrameIntervalMicroseconds = 1_000_000.0 / activeFrameRate;
                            cropSamplingBudget = new PresentSamplingBudget(activeFrameRate);
                            Volatile.Write(ref _activeFrameRate, activeFrameRate);
                            if (activeFrameRate >= Volatile.Read(ref _configuredFrameRate))
                                Interlocked.Exchange(ref _frameRateProtectionActive, 0);
                            AppLog.Info($"Native capture: active cadence is now {activeFrameRate} FPS (selected {config.FrameRate} FPS).");
                        }
                        // NOT locked at this level. Holding gpuLock across the whole
                        // tick was the first attempt and it cost far more than it
                        // looked like it would: the acquire thread then waited on a
                        // lock held through the catch-up loop, the frame clone and
                        // the queue add, and measured avgCopyMapMs went 0.02 -> 7-11
                        // and avgFrameStalenessMs 13 -> 90 with avgPresentGapMs only
                        // 11-27. Ninety milliseconds of stale content is video
                        // lagging wall-clock-timestamped audio, which is worse than
                        // the GPU cost this whole change is removing. The lock lives
                        // inside the encode functions instead, around the D3D work
                        // alone.
                        var lateness = stopwatch.Elapsed - nextTickAt;
                        pacingLatency.Record(lateness > TimeSpan.Zero ? lateness : TimeSpan.Zero);
                        RunPacingTick();
                    }
                    catch (Exception error)
                    {
                        AppLog.Error("Native capture pacing tick failed.", error);
                    }

                    nextTickAt += targetFrameInterval;
                    // Woken very late (a long GC, a suspended thread): snap the
                    // deadline forward instead of spinning out a burst of instant
                    // ticks. RunPacingTick's own catch-up cap has already decided
                    // how much duplicate work a gap is worth.
                    if (nextTickAt < stopwatch.Elapsed - targetFrameInterval) nextTickAt = stopwatch.Elapsed;
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "ClypDat capture pacing"
            };
            pacingThread.Start();
            // From here until the join below, the pacing thread may be inside the
            // encode path touching hwFramesRef, lastHardwareFrame and the D3D device.
            pacingThreadStopped = false;

            while (!token.IsCancellationRequested)
            {
                if (stopwatch.Elapsed - lastDiagLog >= TimeSpan.FromSeconds(2))
                {
                    // Under the GPU lock: half these counters (scaleMs, encodeMs,
                    // frameStalenessMs, the encoded/pad counts) are now written by
                    // the pacing thread, and this both reads and zeroes them. Held
                    // once every two seconds, so it costs the capture nothing.
                    lock (gpuLock)
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
                    long ringBufferCapacityBytes;
                    int ringPacketCount;
                    double ringSpanSeconds;
                    lock (_bufferLock)
                    {
                        ringBufferBytes = _ringBufferBytes;
                        ringBufferCapacityBytes = _ringBufferCapacityBytes;
                        ringPacketCount = _packets.Count;
                        ringSpanSeconds = ringPacketCount > 1
                            ? Math.Max(0, (_packets[^1].WallClockUtc - _packets[0].WallClockUtc).TotalSeconds)
                            : 0;
                    }
                    var ringBufferMb = ringBufferBytes / (1024 * 1024);
                    var ringCapacityMb = ringBufferCapacityBytes / (1024 * 1024);
                    var poolRetainedMb = _packetPayloads.RetainedBytes / (1024 * 1024);
                    // avgEncodeMs now comes from EncodeLoop's own thread (Interlocked
                    // handoff, reset via Exchange so nothing's lost mid-read) - the
                    // capture-thread-local encodeMs is relabeled avgQueueMs, since
                    // it's now just av_frame_clone+TryAdd cost, not the real encode
                    // call. queueDepth/droppedFrames confirm whether decoupling is
                    // actually keeping up: a growing depth or nonzero drops under
                    // load means the encoder itself is too slow, not just blocked.
                    var inputCountSinceLog = Math.Max(1, Interlocked.Exchange(ref _encodeInputCountAccum, 0));
                    var inputMicrosSinceLog = Interlocked.Exchange(ref _encodeInputMicrosAccum, 0);
                    var outputCountSinceLog = Math.Max(1, Interlocked.Exchange(ref _encodeOutputCountAccum, 0));
                    var outputMicrosSinceLog = Interlocked.Exchange(ref _encodeOutputMicrosAccum, 0);
                    var packetCopyCountSinceLog = Math.Max(1, Interlocked.Exchange(ref _packetCopyCountAccum, 0));
                    var packetCopyMicrosSinceLog = Interlocked.Exchange(ref _packetCopyMicrosAccum, 0);
                    var ringInsertCountSinceLog = Math.Max(1, Interlocked.Exchange(ref _ringInsertCountAccum, 0));
                    var ringInsertMicrosSinceLog = Interlocked.Exchange(ref _ringInsertMicrosAccum, 0);
                    var droppedSinceLog = Interlocked.Exchange(ref _encodeDroppedCount, 0);
                    var eagainSinceLog = Interlocked.Exchange(ref _sendRefusedEagainCount, 0);
                    var sendFailedSinceLog = Interlocked.Exchange(ref _sendFailedOtherCount, 0);
                    var packetsOutSinceLog = Interlocked.Exchange(ref _packetsOutCount, 0);
                    var inputFrameCount = framesSeenSinceLog;
                    var wgcCallbackCount = 0L;
                    var dxgiAcquiredCount = 0L;
                    var dxgiTransportedCount = 0L;
                    var dxgiSlotOverwrites = 0L;
                    var dxgiBusySlotSkips = 0L;
                    var dxgiAllBusyDrops = 0L;
                    var dxgiAcquireDuration = TimeSpan.Zero;
                    var dxgiPointerTransportedCount = 0L;
                    var dxgiProducerDuration = TimeSpan.Zero;
                    var dxgiLeaseDuration = TimeSpan.Zero;
                    var wgcTelemetry = wgcCapture?.GetTelemetrySnapshot();
                    var dxgiTelemetry = dxgiCapture?.GetTelemetrySnapshot();
                    if (wgcTelemetry is not null)
                    {
                        var current = wgcTelemetry.Value;
                        wgcCallbackCount = current.CallbackArrivals >= previousWgcTelemetry.CallbackArrivals
                            ? current.CallbackArrivals - previousWgcTelemetry.CallbackArrivals
                            : current.CallbackArrivals;
                        previousWgcTelemetry = current;
                        inputFrameCount = (int)Math.Min(int.MaxValue, wgcCallbackCount);
                    }
                    else if (dxgiTelemetry is not null)
                    {
                        var current = dxgiTelemetry.Value;
                        var sourcePresents = current.AccumulatedPresents >= previousDxgiTelemetry.AccumulatedPresents
                            ? current.AccumulatedPresents - previousDxgiTelemetry.AccumulatedPresents
                            : current.AccumulatedPresents;
                        dxgiAcquiredCount = current.AcquiredFrames >= previousDxgiTelemetry.AcquiredFrames
                            ? current.AcquiredFrames - previousDxgiTelemetry.AcquiredFrames
                            : current.AcquiredFrames;
                        dxgiTransportedCount = current.TransportedFrames >= previousDxgiTelemetry.TransportedFrames
                            ? current.TransportedFrames - previousDxgiTelemetry.TransportedFrames
                            : current.TransportedFrames;
                        dxgiSlotOverwrites = current.OverwrittenFrames >= previousDxgiTelemetry.OverwrittenFrames
                            ? current.OverwrittenFrames - previousDxgiTelemetry.OverwrittenFrames
                            : current.OverwrittenFrames;
                        dxgiBusySlotSkips = current.BusySlotSkips >= previousDxgiTelemetry.BusySlotSkips
                            ? current.BusySlotSkips - previousDxgiTelemetry.BusySlotSkips
                            : current.BusySlotSkips;
                        dxgiPointerTransportedCount = current.TransportedPointerFrames >= previousDxgiTelemetry.TransportedPointerFrames
                            ? current.TransportedPointerFrames - previousDxgiTelemetry.TransportedPointerFrames
                            : current.TransportedPointerFrames;
                        dxgiAllBusyDrops = current.AllBusyDrops >= previousDxgiTelemetry.AllBusyDrops
                            ? current.AllBusyDrops - previousDxgiTelemetry.AllBusyDrops
                            : current.AllBusyDrops;
                        dxgiAcquireDuration = current.AcquireTotal >= previousDxgiTelemetry.AcquireTotal
                            ? current.AcquireTotal - previousDxgiTelemetry.AcquireTotal
                            : current.AcquireTotal;
                        var producerTicks = current.ProducerCopyTotal >= previousDxgiTelemetry.ProducerCopyTotal
                            ? current.ProducerCopyTotal - previousDxgiTelemetry.ProducerCopyTotal
                            : current.ProducerCopyTotal;
                        dxgiProducerDuration = producerTicks;
                        dxgiLeaseDuration = current.AverageLeaseDuration;
                        inputFrameCount = (int)Math.Min(int.MaxValue, sourcePresents);
                        previousDxgiTelemetry = current;
                    }
                    UpdatePeak(ref _peakQueueDepth, encodeQueue.Count);
                    var frameStalenessDenom = Math.Max(1, frameStalenessCount);
                    var wgcInputRate = inputFrameCount / diagElapsed;
                    var wgcUniqueRate = framesProcessedSinceLog / diagElapsed;
                    var wgcTelemetryText = wgcTelemetry is null
                        ? string.Empty
                        : $", wgcCallbackArrivals={wgcCallbackCount}, wgcInputFps={wgcInputRate:0.0}, wgcUniqueFps={wgcUniqueRate:0.0}, wgcPublished={wgcTelemetry.Value.PublishedFrames}, wgcTaken={wgcTelemetry.Value.TakenFrames}, wgcOverwritten={wgcTelemetry.Value.OverwrittenFrames}, wgcCallbackMs={wgcTelemetry.Value.CallbackDurationTotal.TotalMilliseconds:0.0}, wgcGpuLockWaitMs={wgcTelemetry.Value.GpuLockWaitTotal.TotalMilliseconds:0.0}, wgcTimestampGaps={wgcTelemetry.Value.SourceTimestampGapCount}, wgcMaxTimestampGapMs={wgcTelemetry.Value.SourceTimestampGapMaximum.TotalMilliseconds:0.0}, wgcResizeEvents={wgcTelemetry.Value.ResizeEvents}, wgcMinUpdateIntervalAvailable={wgcTelemetry.Value.MinimumUpdateInterval.InterfaceAvailable}, wgcMinUpdateIntervalRequestedMs={wgcTelemetry.Value.MinimumUpdateInterval.Requested.TotalMilliseconds:0.###}, wgcMinUpdateIntervalAppliedMs={wgcTelemetry.Value.MinimumUpdateInterval.Applied?.TotalMilliseconds:0.###}";
                    var dxgiTelemetryText = dxgiTelemetry is null
                        ? string.Empty
                        : $", dxgiSourcePresents={dxgiTelemetry.Value.SourceFrames}, dxgiAcquired={dxgiAcquiredCount}, dxgiTransported={dxgiTransportedCount}, dxgiPublished={dxgiTelemetry.Value.PublishedFrames}, dxgiTaken={dxgiTelemetry.Value.TakenFrames}, dxgiOverwritten={dxgiSlotOverwrites}, dxgiBusySlotSkips={dxgiBusySlotSkips}, dxgiAllBusyDrops={dxgiAllBusyDrops}, dxgiReleaseLagFrames={dxgiTelemetry.Value.ReleaseLagFrames}, dxgiSlots={dxgiTelemetry.Value.SlotCount}, dxgiAcquireMs={(dxgiAcquiredCount == 0 ? 0 : dxgiAcquireDuration.TotalMilliseconds / dxgiAcquiredCount):0.00}, dxgiProducerMs={dxgiProducerDuration.TotalMilliseconds:0.0}, dxgiLeaseMs={dxgiLeaseDuration.TotalMilliseconds:0.00}, dxgiAccumulatedPresents={dxgiTelemetry.Value.AccumulatedPresents}, dxgiZeroPresentSkips={dxgiTelemetry.Value.ZeroPresentFrames}, dxgiPointerUpdates={dxgiTelemetry.Value.PointerUpdates}, dxgiPointerTransported={dxgiPointerTransportedCount}";
                    var outputFrameRate = packetsOutSinceLog / diagElapsed;
                    var foregroundForDiagnostics = isMonitorMode || IsWindowForegroundAndVisible(targetHandle);
                    var capturePaused = Volatile.Read(ref _capturePaused) != 0 || (!isMonitorMode && !foregroundForDiagnostics);
                    if (packetsOutSinceLog > 0) encoderHasProducedPacket = true;
                    AppLog.Debug($"Native capture diag: encodePath={(hardwareFramesActive ? "D3D11 zero-copy" : "System memory")}, inputFps={inputFrameCount / diagElapsed:0.0}, freshFps={framesProcessedSinceLog / diagElapsed:0.0}, outputFps={outputFrameRate:0.0}, avgCopyReadbackMs={copyMapMs / n:0.00}, framesSeen={framesSeen}, pointerFramesSeen={pointerFramesSeenSinceLog}, framesEncoded={framesEncoded}, ringPackets={ringPacketCount}, ringSpanSeconds={ringSpanSeconds:0.0}, ringBufferMb={ringBufferMb}, ringCapacityMb={ringCapacityMb}, packetPoolMb={poolRetainedMb}, sendFrameMs={inputMicrosSinceLog / 1000.0 / inputCountSinceLog:0.00}, packetReceiveMs={outputMicrosSinceLog / 1000.0 / outputCountSinceLog:0.00}, packetCopyMs={packetCopyMicrosSinceLog / 1000.0 / packetCopyCountSinceLog:0.00}, ringInsertMs={ringInsertMicrosSinceLog / 1000.0 / ringInsertCountSinceLog:0.00}, avgScaleMs={scaleMs / n:0.00}, avgQueueMs={encodeMs / n:0.00}, queueDepth={encodeQueue.Count}, pendingEncoderFrames={Volatile.Read(ref _pendingEncoderFrames)}, peakPendingEncoderFrames={Volatile.Read(ref _peakPendingEncoderFrames)}, droppedFrames={droppedSinceLog}, padsSkipped={padsSkippedSinceLog}, framesQueuedSinceLog={framesEncodedSinceLog}, packetsOut={packetsOutSinceLog}, rollingOutputFps={outputFrameRate:0.0}, sendEagain={eagainSinceLog}, sendFailed={sendFailedSinceLog}, avgWaitMs={waitMs / m:0.00}, avgGetFrameMs={getFrameMs / m:0.00}, avgPreAcquireMs={preAcquireMs / m:0.00}, maxPreAcquireMs={preAcquireMaxMs:0.00}, maxFrameStalenessMs={frameStalenessMaxMs:0.00}, iterations={iterationsSinceLog}, cropCopies={cropCopies}, cropCopiesSkipped={cropCopiesSkipped}, zeroPresentSkips={zeroPresentSkips}, avgAccumulatedFrames={(double)accumulatedFramesSum / realFrameCount:0.00}, maxAccumulatedFrames={accumulatedFramesMax}, avgPresentGapMs={presentGapSumMs / presentGapDenom:0.00}, maxPresentGapMs={presentGapMaxMs:0.00}, managedMb={managedMb}, gen0={GC.CollectionCount(0)}, gen1={GC.CollectionCount(1)}, gen2={GC.CollectionCount(2)}{wgcTelemetryText}{dxgiTelemetryText}.");
                    // Raw encoded rate, deliberately NOT crediting suppressed pads
                    // back in. It remains useful telemetry, but a low rate with
                    // an empty queue is a pacing/source shortfall, not encoder
                    // overload and must not trigger quality advice.
                    // The encoder's returned packets are the actual output;
                    // queued AVFrames can still be delayed or rejected.
                    var encoderPressure = droppedSinceLog > 0 || encodeQueue.Count * 4 >= encodeQueueCapacity * 3;
                    var saveInProgress = Volatile.Read(ref _savesInFlight) > 0;
                    var sourceFrameRate = inputFrameCount / diagElapsed;
                    var freshTransportRate = framesProcessedSinceLog / diagElapsed;
                    var (pacingP95Ms, pacingMaxMs) = pacingLatency.SnapshotAndReset();
                    var (processingP95Ms, processingMaxMs) = processingLatency.SnapshotAndReset();
                    var (submissionP95Ms, submissionMaxMs) = submissionLatency.SnapshotAndReset();
                    var (outputLatencyP95Ms, outputLatencyMaxMs) = outputLatency.SnapshotAndReset();
                    var missedPacingSinceLog = Interlocked.Exchange(ref pacingMissedFrames, 0);
                    var queueReplacementsSinceLog = Interlocked.Exchange(ref encodeQueueReplacements, 0);
                    var outputShortfall = hasCapturedRealFrame && outputFrameRate < activeFrameRate * ReplayEncoderQualificationPolicy.TargetThreshold;
                    var bottleneckStage = ReplayPipelineHealthClassifier.Classify(
                        sourceFrameRate, freshTransportRate, processingP95Ms, 1000.0 / activeFrameRate,
                        missedPacingSinceLog, encodeQueue.Count, encodeQueueCapacity, submissionP95Ms,
                        outputLatencyP95Ms, outputShortfall);
                    AppLog.Debug($"Native capture throughput: encodePath={(hardwareFramesActive ? "D3D11 zero-copy" : "System memory")}, inputFps={inputFrameCount / diagElapsed:0.0}, freshFps={framesProcessedSinceLog / diagElapsed:0.0}, outputFps={outputFrameRate:0.0}, avgCopyReadbackMs={copyMapMs / n:0.00}, queueDepth={encodeQueue.Count}, queueCapacity={encodeQueueCapacity}, droppedFrames={droppedSinceLog}, stage={bottleneckStage}, pacingMisses={missedPacingSinceLog}, pacingP95Ms={pacingP95Ms:0.00}, pacingMaxMs={pacingMaxMs:0.00}, queueReplacements={queueReplacementsSinceLog}, processingP95Ms={processingP95Ms:0.00}, processingMaxMs={processingMaxMs:0.00}, submissionP95Ms={submissionP95Ms:0.00}, submissionMaxMs={submissionMaxMs:0.00}, outputLatencyP95Ms={outputLatencyP95Ms:0.00}, outputLatencyMaxMs={outputLatencyMaxMs:0.00}.");
                    var encoderSubmissionStalled = encoderPressure && outputFrameRate < activeFrameRate * 0.5;
                    var sourceRecoverySample = new ReplayCaptureHealth("Native", "DXGI", ReplayCaptureState.Degraded,
                        activeFrameRate, inputFrameCount / diagElapsed, freshTransportRate, outputFrameRate, 0, droppedSinceLog,
                        encodeQueue.Count, encoderName, "Default adapter", string.Empty, DateTime.UtcNow)
                    {
                        EncodeQueueCapacity = encodeQueueCapacity,
                        CapturePaused = capturePaused,
                        SaveInProgress = saveInProgress,
                        EncoderSubmissionStalled = encoderSubmissionStalled,
                        DegradeReason = isStalled ? ReplayDegradeReason.CaptureStall : ReplayDegradeReason.None,
                        TransportFrameRate = dxgiTransportedCount / diagElapsed,
                        TransportBusySlotSkips = dxgiBusySlotSkips,
                        TransportAllBusyDrops = dxgiAllBusyDrops,
                        TransportReleaseLagFrames = dxgiTelemetry?.ReleaseLagFrames ?? 0,
                        SurfacesInUse = dxgiTelemetry is null
                            ? 0
                            : (int)Math.Min(dxgiTelemetry.Value.SlotCount, Math.Max(0, dxgiTelemetry.Value.ReleaseLagFrames)),
                        SurfaceCapacity = dxgiTelemetry?.SlotCount ?? 0
                    };
                    var sourceRecoveryAction = wgcCapture is null && dxgiCapture is not null
                        ? sourceRecovery.Observe(sourceRecoverySample, foregroundForDiagnostics, DateTime.UtcNow)
                        : CaptureSourceRecoveryAction.None;
                    if (sourceRecoveryAction == CaptureSourceRecoveryAction.RecreateDxgi)
                    {
                        try
                        {
                            dxgiCapture!.Dispose();
                            dxgiCapture = DesktopDuplicationFrameSource.Create(device, targetHandle, config, out desktopBounds);
                            activeGameFrameSource = dxgiCapture;
                            previousDxgiTelemetry = default;
                            AppLog.Info("Native capture: DXGI source starvation persisted for two windows; recreated acquisition source.");
                        }
                        catch (Exception error)
                        {
                            AppLog.Error("Native capture: DXGI source recreation failed.", error);
                            dxgiCapture = null;
                        }
                    }
                    else if (sourceRecoveryAction == CaptureSourceRecoveryAction.SwitchToWgc && !isMonitorMode)
                    {
                        try
                        {
                            var fallback = WindowGraphicsCaptureSource.Create(device, gpuLock, targetHandle, config.CaptureCursor, activeFrameRate);
                            dxgiCapture!.Dispose();
                            dxgiCapture = null;
                            wgcCapture = fallback;
                            activeGameFrameSource = fallback;
                            var size = fallback.ContentSize;
                            desktopBounds = new Vortice.RawRect(0, 0, size.Width, size.Height);
                            AppLog.Info("Native capture: DXGI source starvation repeated within 30s; switched to bounded WGC.");
                        }
                        catch (Exception error)
                        {
                            AppLog.Error("Native capture: WGC fallback after DXGI source starvation failed.", error);
                        }
                    }
                    var wgcCongested = wgcCapture is not null && !capturePaused && !saveInProgress &&
                        outputFrameRate < activeFrameRate * 0.5 && encoderPressure;
                    consecutiveWgcCongestionWindows = wgcCongested ? consecutiveWgcCongestionWindows + 1 : 0;
                    var pipelineAction = sourceRecoveryAction switch
                    {
                        CaptureSourceRecoveryAction.RecreateDxgi => ReplayPipelineRecoveryAction.RecreateDxgi,
                        CaptureSourceRecoveryAction.SwitchToWgc => ReplayPipelineRecoveryAction.SwitchToWgc,
                        _ when consecutiveWgcCongestionWindows >= 3 => ReplayPipelineRecoveryAction.RestartWorker,
                        _ => ReplayPipelineRecoveryAction.None
                    };
                    var freshTransportTarget = Math.Min(activeFrameRate, sourceFrameRate);
                    var transportShortfall = hasCapturedRealFrame && freshTransportTarget > 0 &&
                        freshTransportRate < freshTransportTarget * ReplayEncoderQualificationPolicy.TargetThreshold;
                    if (transportShortfall && !saveInProgress)
                        consecutiveTransportShortfallWindows++;
                    else
                        consecutiveTransportShortfallWindows = 0;
                    var transportDegraded = consecutiveTransportShortfallWindows >= ReplayEncoderFailoverPolicy.RequiredOverloadWindows;
                    var encoderStage = bottleneckStage is ReplayPipelineStage.EncodeQueue or ReplayPipelineStage.EncoderSubmission or ReplayPipelineStage.EncoderCompletion;
                    var pipelineUnhealthy = ReplaySaveRecoveryPolicy.RequiresQuarantine(
                        capturePaused, sourceRecoveryAction, isStalled, transportDegraded);
                    UpdateRecoveryTimeline(pipelineUnhealthy, capturePaused, DateTime.UtcNow);
                    if (!isMonitorMode && wgcCapture is null && dxgiCapture is not null &&
                        dxgiCadenceFallback.ShouldFallback(activeFrameRate, freshTransportRate,
                            IsWindowForegroundAndVisible(targetHandle), encoderPressure, saveInProgress))
                    {
                        try
                        {
                            var fallback = WindowGraphicsCaptureSource.Create(device, gpuLock, targetHandle, config.CaptureCursor, activeFrameRate);
                            dxgiCapture.Dispose();
                            dxgiCapture = null;
                            wgcCapture = fallback;
                            activeGameFrameSource = fallback;
                            dxgiCadenceFallback.MarkFallbackCommitted();
                            var size = fallback.ContentSize;
                            desktopBounds = new Vortice.RawRect(0, 0, size.Width, size.Height);
                            AppLog.Info($"Native capture: DXGI fresh frames remained below target; switched to WGC ({freshTransportRate:0.0}/{activeFrameRate} FPS).");
                        }
                        catch (Exception error)
                        {
                            AppLog.Error("Native capture: DXGI cadence fallback could not start WGC; keeping DXGI.", error);
                        }
                    }
                    var overloaded = encoderStage;
                    if (hasCapturedRealFrame && encoderHasProducedPacket && !saveInProgress && overloaded)
                        consecutiveOverloadWindows++;
                    else if (!overloaded || saveInProgress)
                        consecutiveOverloadWindows = 0;

                    if (hasCapturedRealFrame && consecutiveOverloadWindows >= ReplayEncoderFailoverPolicy.RequiredOverloadWindows &&
                        !string.IsNullOrEmpty(activeEncoderCandidate.Name))
                    {
                        if (fullSessionFormatContext is not null)
                        {
                            // One continuously-written MP4 cannot change codec
                            // parameter sets mid-track. Protect full-session
                            // integrity by reducing cadence; replay-window mode
                            // can swap safely because packets and extradata are
                            // tagged per encoder generation.
                            var lower = NextLowerReplayFrameRate(activeFrameRate);
                            if (Volatile.Read(ref _frameRateProtectionEnabled) != 0 && lower < activeFrameRate)
                            {
                                Interlocked.Exchange(ref _requestedFrameRate, lower);
                                Interlocked.Exchange(ref _frameRateProtectionActive, 1);
                                AppLog.Info($"Native capture: full-session encoder congestion requested {activeFrameRate}->{lower} FPS; encoder remains fixed to keep the MP4 track valid.");
                            }
                            else
                            {
                                AppLog.Info("Native capture: full-session encoder congestion persists at minimum protected cadence; preserving current MP4 encoder.");
                            }
                            consecutiveOverloadWindows = 0;
                        }
                        else
                        {
                            var switched = false;
                            foreach (var candidate in ReplayEncoderFailoverPolicy.CandidatesAfter(
                                         config.VideoCodec, config.EncoderMode, activeEncoderCandidate, attemptedEncoderCandidates))
                            {
                                attemptedEncoderCandidates.Add(candidate);
                                try
                                {
                                    var replacement = CreateEncoder(config, outputWidth, outputHeight, hwFramesRef, device,
                                        out var replacementTimeBase, out var replacementName, out var replacementHardware,
                                        candidateOrder: new[] { candidate });
                                    var swapped = new ManualResetEventSlim(false);
                                    swapCompletionEvents.Add(swapped);
                                    lock (gpuLock)
                                    {
                                        // Stop pacing while the control job crosses
                                        // the queue. Every job before it uses the old
                                        // frame type; every job after it sees the new
                                        // hardwareFramesActive value.
                                        lock (encodeQueueGate)
                                            encodeQueue!.Add(new EncodeJob(0, DateTime.UtcNow, (nint)replacement, swapped));
                                        if (!swapped.Wait(TimeSpan.FromSeconds(5)))
                                        {
                                            // Event and context stay alive until the
                                            // encode thread joins. Ending this capture
                                            // is safer than racing a late control job.
                                            retiredCodecContexts.Add((nint)replacement);
                                            throw new TimeoutException($"Encoder failover to {candidate.Name}/{candidate.InputPath} did not complete within 5 seconds.");
                                        }

                                        retiredCodecContexts.Add((nint)codecContext);
                                        codecContext = replacement;
                                        _timeBase = replacementTimeBase;
                                        _videoCodecId = replacement->codec_id;
                                        encoderName = replacementName;
                                        hardwareFramesActive = replacementHardware;
                                        requiresDistinctAmfSoftwareFrame = !replacementHardware && replacementName.Contains("amf", StringComparison.OrdinalIgnoreCase);
                                        activeEncoderCandidate = ResolveEncoderCandidate(config, replacementName, replacementHardware);
                                        if (!replacementHardware && hwFramesRef != 0)
                                        {
                                            if (lastHardwareFrame is not null)
                                            {
                                                var staleHardwareFrame = lastHardwareFrame;
                                                ffmpeg.av_frame_free(&staleHardwareFrame);
                                                lastHardwareFrame = null;
                                            }
                                            hardwarePoolTextures.Clear();
                                            ReleaseHardwareFrames(ref hwDeviceRef, ref hwFramesRef);
                                        }
                                    }
                                    encoderHasProducedPacket = false;
                                    consecutiveOverloadWindows = 0;
                                    switched = true;
                                    AppLog.Info($"Native capture: sustained encoder congestion switched to {replacementName}/{activeEncoderCandidate.InputPath}; replay retained by encoder generation.");
                                    break;
                                }
                                catch (TimeoutException)
                                {
                                    throw;
                                }
                                catch (Exception error)
                                {
                                    AppLog.Info($"Native capture: encoder failover candidate {candidate.Name}/{candidate.InputPath} unavailable ({error.Message}).");
                                }
                            }
                            if (!switched)
                            {
                                consecutiveOverloadWindows = 0;
                                var protectionEnabled = Volatile.Read(ref _frameRateProtectionEnabled) != 0;
                                var lower = ReplayEncoderFailoverPolicy.ProtectedFrameRateAfterHardwareExhaustion(activeFrameRate, protectionEnabled);
                                if (lower < activeFrameRate)
                                {
                                    Interlocked.Exchange(ref _requestedFrameRate, lower);
                                    Interlocked.Exchange(ref _frameRateProtectionActive, 1);
                                    AppLog.Info($"Native capture: every remaining hardware encoder candidate was exhausted; retaining {encoderName} and protecting game load at {activeFrameRate}->{lower} FPS.");
                                }
                                else
                                {
                                    AppLog.Info("Native capture: every remaining hardware encoder candidate was exhausted; keeping current encoder at the minimum protected cadence.");
                                }
                            }
                        }
                    }
                    // A stall is worse than an overload and reads nothing like one:
                    // no frames arrive at all, so nothing gets dropped and the queue
                    // stays empty - every overload signal above says "healthy" right
                    // up until the clip comes out frozen. Report it while it is
                    // happening instead, so the UI can say so live rather than the
                    // save-time warning being the first anyone hears of it.
                    var sourceStarved = sourceRecoveryAction != CaptureSourceRecoveryAction.None;
                    if (isStalled || transportDegraded || sourceStarved) _lastDegradedUtc = DateTime.UtcNow;
                    if (overloaded)
                    {
                        _lastDegradedUtc = DateTime.UtcNow;
                        // Same numbers as the DEBUG diag line above, but at INFO so
                        // an overload shows up in the log a user would actually
                        // send without needing a full diagnostics export - the
                        // debug log is 1-8MB/day and isn't something to ask for
                        // first when triaging "clips are choppy."
                        const string recoveryGuidance = "Automatic hardware profile is already active; reduce capture resolution or frame rate.";
                        AppLog.Info($"Native capture: overload - dropped {droppedSinceLog} frame(s) in the last {diagElapsed:0.0}s, queue {encodeQueue.Count}/{encodeQueueCapacity}, avgInputMs={inputMicrosSinceLog / 1000.0 / inputCountSinceLog:0.0}, avgOutputMs={outputMicrosSinceLog / 1000.0 / outputCountSinceLog:0.0}, avgScaleMs={scaleMs / n:0.0}. {recoveryGuidance}");
                    }
                    var activeCaptureMode = wgcCapture is null ? dxgiCapture?.CaptureMode ?? "Game Capture" : "Windows Graphics Capture (recovery)";
                    SetHealth(new ReplayCaptureHealth("Native", activeCaptureMode,
                        pipelineAction == ReplayPipelineRecoveryAction.SwitchToWgc ? ReplayCaptureState.Recovering :
                        overloaded || isStalled || transportDegraded || sourceStarved ? ReplayCaptureState.Degraded : ReplayCaptureState.Healthy,
                        activeFrameRate, inputFrameCount / diagElapsed, framesProcessedSinceLog / diagElapsed,
                        outputFrameRate, Math.Max(0, packetsOutSinceLog - framesProcessedSinceLog), droppedSinceLog, encodeQueue.Count,
                        encoderName, "Default adapter",
                        sourceStarved ? "Capture source starved; recovering DXGI acquisition." :
                        isStalled ? "Capture stalled - no new frames from the display. Recovering." :
                        overloaded ? "Capture overload. Output may fall below target FPS." :
                        transportDegraded ? "Game Capture transport is below 99% of source/selected cadence." :
                        outputShortfall ? "Capture cadence below target; encoder queue is keeping up." : string.Empty,
                        DateTime.UtcNow)
                    {
                        TotalDroppedFrames = Interlocked.Read(ref _totalDroppedFrames),
                        PeakQueueDepth = Volatile.Read(ref _peakQueueDepth),
                        LastDegradedUtc = _lastDegradedUtc,
                        // Stall wins when both are true: no frames are arriving,
                        // so whatever the encoder looks like is a consequence of
                        // that rather than the encode settings being too costly.
                        DegradeReason = sourceStarved || isStalled ? ReplayDegradeReason.CaptureStall
                            : transportDegraded ? ReplayDegradeReason.CaptureTransport
                            : ReplayPipelineHealthClassifier.ToDegradeReason(bottleneckStage),
                        BottleneckStage = bottleneckStage,
                        PacingMissedFrames = missedPacingSinceLog,
                        PacingLatenessP95Ms = pacingP95Ms,
                        PacingLatenessMaxMs = pacingMaxMs,
                        EncodeQueueReplacements = queueReplacementsSinceLog,
                        ProcessingP95Ms = processingP95Ms,
                        ProcessingMaxMs = processingMaxMs,
                        SubmissionP95Ms = submissionP95Ms,
                        SubmissionMaxMs = submissionMaxMs,
                        EncoderOutputLatencyP95Ms = outputLatencyP95Ms,
                        EncoderOutputLatencyMaxMs = outputLatencyMaxMs,
                        AdapterDescription = adapterDescription,
                        EncoderProfile = config.EncoderProfile,
                        EncodeQueueCapacity = encodeQueueCapacity,
                        FrameRateMode = ReplayFrameTimingPolicy.Normalize(config.FrameRateMode),
                        ConfiguredFrameRate = Volatile.Read(ref _configuredFrameRate),
                        EncoderInputPath = hardwareFramesActive ? "D3D11 zero-copy" : "System memory",
                        FrameRateProtectionActive = Volatile.Read(ref _frameRateProtectionEnabled) != 0 &&
                            Volatile.Read(ref _frameRateProtectionActive) != 0,
                        // See ReplayCaptureHealth.SaveInProgress. A save backs
                        // the queue up for a second or two by design; it is not
                        // evidence the settings are unsustainable.
                        SaveInProgress = Volatile.Read(ref _savesInFlight) > 0,
                        WgcRequestedUpdateInterval = wgcTelemetry?.MinimumUpdateInterval.Requested,
                        WgcAppliedUpdateInterval = wgcTelemetry?.MinimumUpdateInterval.Applied,
                        AcquiredFrameRate = dxgiAcquiredCount / diagElapsed,
                        TransportFrameRate = dxgiTransportedCount / diagElapsed,
                        TransportSlotOverwrites = dxgiSlotOverwrites,
                        TransportBusySlotSkips = dxgiBusySlotSkips,
                        TransportAllBusyDrops = dxgiAllBusyDrops,
                        TransportReleaseLagFrames = dxgiTelemetry?.ReleaseLagFrames ?? 0,
                        SurfacesInUse = dxgiTelemetry is null
                            ? 0
                            : (int)Math.Min(dxgiTelemetry.Value.SlotCount, Math.Max(0, dxgiTelemetry.Value.ReleaseLagFrames)),
                        SurfaceCapacity = dxgiTelemetry?.SlotCount ?? 0,
                        ProducerGpuDuration = dxgiProducerDuration,
                        AverageTransportLeaseDuration = dxgiLeaseDuration,
                        PointerUpdateFrameRate = dxgiPointerTransportedCount / diagElapsed,
                        StartupPhase = ReplayCaptureStartupPhase.Ready,
                        CapturePaused = capturePaused,
                        RecoveryCleanSinceUtc = _recoveryCleanSinceUtc,
                        PipelineRecoveryAction = pipelineAction,
                        EncoderSubmissionStalled = encoderSubmissionStalled,
                        ProcessingGpuPriority = processingGpuPriority,
                        AcquisitionGpuPriority = dxgiCapture?.AppliedGpuPriority
                    });
                    copyMapMs = 0;
                    scaleMs = 0;
                    encodeMs = 0;
                    frameStalenessMs = 0;
                    frameStalenessMaxMs = 0;
                    frameStalenessCount = 0;
                    framesEncodedSinceLog = 0;
                    framesSeenSinceLog = 0;
                    pointerFramesSeenSinceLog = 0;
                    framesProcessedSinceLog = 0;
                    padsSkippedSinceLog = 0;
                    waitMs = 0;
                    getFrameMs = 0;
                    iterationsSinceLog = 0;
                    cropCopies = 0;
                    cropCopiesSkipped = 0;
                    zeroPresentSkips = 0;
                    accumulatedFramesSum = 0;
                    accumulatedFramesMax = 0;
                    presentGapSumMs = 0;
                    presentGapCount = 0;
                    presentGapMaxMs = 0;
                    preAcquireMs = 0;
                    preAcquireMaxMs = 0;
                    }
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
                        activeGameFrameSource = null;

                        var freshMonitor = ResolveTargetMonitor(targetHandle, config);
                        if (wgcCapture is not null || freshMonitor != targetMonitor || duplication is null)
                        {
                            targetMonitor = freshMonitor;
                            wgcCapture?.Dispose();
                            wgcCapture = null;
                            dxgiCapture?.Dispose();
                            dxgiCapture = null;
                            duplication?.Dispose();
                            duplication = null;
                            try
                            {
                                duplication = CreateDuplicationFor(device, targetHandle, config, out desktopBounds);
                                AppLog.Info("Native capture: DXGI duplication replaced for the new target.");
                            }
                            catch (Exception error) when (!isMonitorMode)
                            {
                                AppLog.Error("Native capture: DXGI target replacement failed; using bounded WGC recovery source.", error);
                                wgcCapture = WindowGraphicsCaptureSource.Create(device, gpuLock, targetHandle, config.CaptureCursor, activeFrameRate);
                                activeGameFrameSource = wgcCapture;
                                var size = wgcCapture.ContentSize;
                                desktopBounds = new Vortice.RawRect(0, 0, size.Width, size.Height);
                            }
                        }

                        if (isPaused)
                        {
                            isPaused = false;
                            lock (_bufferLock) _pauseEvents.Add(new PauseEvent(MonotonicClock.UtcNow, false));
                        }
                        previousWgcTelemetry = default;
                        previousDxgiTelemetry = default;
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
                if (wgcCapture is null && dxgiCapture is null && duplication is null)
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
                        if (!isMonitorMode)
                        {
                            try
                            {
                                wgcCapture = WindowGraphicsCaptureSource.Create(device, gpuLock, targetHandle, config.CaptureCursor, activeFrameRate);
                                activeGameFrameSource = wgcCapture;
                                var size = wgcCapture.ContentSize;
                                desktopBounds = new Vortice.RawRect(0, 0, size.Width, size.Height);
                                AppLog.Info("Native capture: DXGI recovery failed; switched to bounded WGC.");
                            }
                            catch (Exception wgcError)
                            {
                                AppLog.Error("Native capture: WGC recovery source could not start.", wgcError);
                            }
                        }
                    }
                    continue;
                }

                var usingWgc = wgcCapture is not null;
                // Both sources now publish application-owned textures. DXGI's
                // producer has already copied and released its acquired frame
                // before this consumer begins crop/scale work.
                var frameInfo = new OutduplFrameInfo();
                var acquireResultCode = ResultCode.WaitTimeout.Code;
                var selectedGameFrameSource = activeGameFrameSource ?? (IGameFrameSource?)dxgiCapture;
                GameFrameLease? frameLease = null;
                var hasDesktopContentUpdate = false;
                var hasPointerUpdate = false;
                var contentTimestamp = 0L;
                var duplicationFrameAcquired = false;
                ID3D11Resource? desktopResource = null;
                if (selectedGameFrameSource is not null)
                {
                    selectedGameFrameSource.WaitAndTakeLatestFrame(
                        TimeSpan.FromMilliseconds(Math.Max(100d, acquireTimeoutMs * 4d)), token, out frameLease);
                    hasDesktopContentUpdate = frameLease?.HasDesktopContentUpdate ?? false;
                    hasPointerUpdate = frameLease?.HasPointerUpdate ?? false;
                    contentTimestamp = frameLease?.ContentTimestamp ?? 0;
                    if (frameLease is not null)
                    {
                        frameInfo = new OutduplFrameInfo
                        {
                            LastPresentTime = frameLease.SourceTimestamp,
                            AccumulatedFrames = (uint)frameLease.AccumulatedPresents
                        };
                        desktopResource = frameLease.Texture.QueryInterface<ID3D11Resource>();
                    }
                }
                else if (duplication is not null)
                {
                    // A 240 Hz source made this loop call AcquireNextFrame about
                    // 200 times per second even though only 60 frames could be
                    // recorded. Windows bills GPU scheduling work per call, so
                    // those discarded acquisitions directly stole game budget.
                    // Pace before acquisition and ask DXGI for the newest frame
                    // at the selected cadence; the encode clock is independent.
                    if (nextDxgiAcquireAt == TimeSpan.Zero) nextDxgiAcquireAt = stopwatch.Elapsed;
                    while (nextDxgiAcquireAt > stopwatch.Elapsed)
                    {
                        token.ThrowIfCancellationRequested();
                        var remaining = nextDxgiAcquireAt - stopwatch.Elapsed;
                        if (remaining > TimeSpan.FromMilliseconds(2))
                            Thread.Sleep(remaining - TimeSpan.FromMilliseconds(1));
                        else
                            Thread.Yield();
                    }

                    var scheduledAcquireAt = nextDxgiAcquireAt;
                    var acquireResult = duplication.AcquireNextFrame(acquireTimeoutMs, out frameInfo, out var dxgiResource);
                    nextDxgiAcquireAt = NextDxgiAcquireDeadline(
                        scheduledAcquireAt,
                        stopwatch.Elapsed,
                        Volatile.Read(ref _activeFrameRate));
                    acquireResultCode = acquireResult.Code;
                    duplicationFrameAcquired = acquireResult.Success;
                    hasDesktopContentUpdate = frameInfo.LastPresentTime != 0;
                    hasPointerUpdate = config.CaptureCursor && frameInfo.LastMouseUpdateTime != 0;
                    contentTimestamp = frameInfo.LastPresentTime;
                    if (acquireResult.Success && dxgiResource is not null)
                    {
                        try { desktopResource = dxgiResource.QueryInterface<ID3D11Resource>(); }
                        finally { dxgiResource.Dispose(); }
                    }
                    else
                    {
                        dxgiResource?.Dispose();
                    }
                }
                if (desktopResource is null && selectedGameFrameSource is not null && !string.IsNullOrWhiteSpace(selectedGameFrameSource.Failure))
                {
                    if (usingWgc)
                    {
                        AppLog.Info($"Native capture: WGC source closed ({selectedGameFrameSource.Failure}); restarting WGC.");
                        wgcCapture!.Dispose();
                        try { wgcCapture = WindowGraphicsCaptureSource.Create(device, gpuLock, targetHandle, config.CaptureCursor, activeFrameRate); activeGameFrameSource = wgcCapture; }
                        catch (Exception error) { throw new InvalidOperationException("Windows.Graphics.Capture could not restart for game capture.", error); }
                    }
                    else
                    {
                        AppLog.Info($"Native capture: DXGI producer stopped ({selectedGameFrameSource.Failure}); recreating.");
                        dxgiCapture?.Dispose();
                        dxgiCapture = null;
                        activeGameFrameSource = null;
                    }
                }
                waitMs += stageStopwatch.Elapsed.TotalMilliseconds;

                var occluded = !isMonitorMode && !usingWgc && !IsWindowForegroundAndVisible(targetHandle);

                if (desktopResource is not null)
                {
                    consecutiveAcquireFailures = 0;
                    if (!usingWgc && !hasDesktopContentUpdate && !hasPointerUpdate)
                    {
                        zeroPresentSkips++;
                        if (duplicationFrameAcquired) duplication?.ReleaseFrame();
                        desktopResource.Dispose();
                        frameLease?.Dispose();
                    }
                    else
                    {
                        if (hasDesktopContentUpdate)
                        {
                            accumulatedFramesSum += frameInfo.AccumulatedFrames;
                            if (frameInfo.AccumulatedFrames > accumulatedFramesMax) accumulatedFramesMax = frameInfo.AccumulatedFrames;
                            if (lastRealPresentTicks != 0)
                            {
                                var gapMs = (contentTimestamp - lastRealPresentTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                                presentGapSumMs += gapMs;
                                presentGapCount++;
                                if (gapMs > presentGapMaxMs) presentGapMaxMs = gapMs;
                            }
                            lastRealPresentTicks = contentTimestamp;
                        }

                        // The crop, the Blt and the crop-size rebuild all share the
                        // D3D11 immediate context - and croppedTexture / staging /
                        // swsContext - with the pacing tick's encode path, which
                        // now runs on its own thread. The lock spans this whole
                        // per-frame body rather than each call, because those
                        // resources have to stay coherent across the crop-size
                        // rebuild, not merely during one call. The tick side is
                        // deliberately much narrower (see EncodeScheduledFrame*):
                        // locking there at tick granularity starved this thread and
                        // pushed frame staleness to 90ms.
                        Monitor.Enter(gpuLock);
                        try
                        {
                            framesSeen++;
                            framesSeenSinceLog++;
                            if (hasPointerUpdate) pointerFramesSeenSinceLog++;
                            // Pointer movement is fresh visual output, not proof
                            // that desktop pixels resumed. Keep the watchdog and
                            // content cadence tied to real desktop presents.
                            if (hasDesktopContentUpdate && isStalled)
                            {
                                isStalled = false;
                                AppLog.Info($"Native capture: frames resumed after a {(stopwatch.Elapsed - lastRealFrameElapsed).TotalSeconds:0.#}s stall.");
                            }
                            if (hasDesktopContentUpdate)
                            {
                                lastRealFrameElapsed = stopwatch.Elapsed;
                                recoveryAttempts = 0;
                                recoveryRetryInterval = baseRecoveryRetryInterval;
                            }

                            stageStopwatch.Restart();
                            int cropLeft = 0, cropTop = 0, cropWidth = captureWidth, cropHeight = captureHeight;
                            if (usingWgc)
                            {
                                (cropWidth, cropHeight) = wgcCapture!.ContentSize;
                            }
                            else if (isMonitorMode)
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

                                        // The video processor and its enumerator bake the INPUT
                                        // size into their content description, so they are stale
                                        // now too. Only the staging texture, the CPU scaler and
                                        // the crop view were being rebuilt, leaving the processor
                                        // configured for the previous input size. The output-sized
                                        // resources (nv12Output, the staging ring, outputView) do
                                        // not depend on the crop and are deliberately kept.
                                        try
                                        {
                                            videoProcessor?.Dispose();
                                            vpEnumerator?.Dispose();
                                            (vpEnumerator, videoProcessor) = CreateVideoProcessorForSize(
                                                videoDevice!, captureWidth, captureHeight, outputWidth, outputHeight, activeFrameRate);
                                            (croppedTexture, inputView) = CreateGpuCropInputView(device, videoDevice!, vpEnumerator, captureWidth, captureHeight);

                                            // The cursor overlay's input view was created from the
                                            // enumerator just replaced, so it has to be rebuilt with it.
                                            if (gpuCursorAvailable)
                                            {
                                                cursorInputView?.Dispose();
                                                cursorTexture?.Dispose();
                                                (cursorTexture, cursorInputView) = CreateGpuCursorOverlay(device, videoDevice!, vpEnumerator);
                                            }
                                        }
                                        catch (Exception error)
                                        {
                                            // Rebuilding the processor mid-session failed. Fall back
                                            // to the CPU scaler - which was just recreated for the new
                                            // size above - rather than blitting through a processor
                                            // that no longer matches its input.
                                            AppLog.Error("Native capture: GPU scaler could not be rebuilt for the new crop size; falling back to CPU scaling.", error);
                                            videoProcessor = null;
                                            vpEnumerator = null;
                                            croppedTexture = null;
                                            inputView = null;
                                            cursorTexture = null;
                                            cursorInputView = null;
                                            gpuCursorAvailable = false;
                                            useGpuScale = false;
                                        }

                                        // The texture the pending crop lived in is gone - the
                                        // replacement holds nothing yet, so there is nothing
                                        // for the next encode tick to convert until a fresh
                                        // present lands in it.
                                        croppedDirty = false;
                                        nv12Ready = false;
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
                                // Whether this present's pixels actually reached the
                                // pipeline, as opposed to being superseded by a
                                // newer present already sitting unconsumed in
                                // croppedTexture - see the crop-copy gate below.
                                // Always true on the CPU path, which has nowhere to
                                // hold a pending present and so must convert each one.
                                var contentAdvanced = false;
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
                                    // ...but not necessarily on EVERY present. The loop
                                    // polls at roughly twice the target rate (see
                                    // acquireTimeoutMs), and a source presenting faster
                                    // than the target hands us several presents per
                                    // encode tick, all but the last of which are
                                    // overwritten in croppedTexture before anything ever
                                    // reads it. This is a full-resolution BGRA copy - at
                                    // 1440p that is ~14MB of copy bandwidth thrown away
                                    // per wasted present, which on an iGPU comes straight
                                    // out of the same system memory the game is using.
                                    //
                                    // This is NOT the throttle described above that was
                                    // reverted. That one asked "is an encode due yet?" at
                                    // each present's arrival, so it could refuse a present
                                    // while croppedTexture held nothing - i.e. while that
                                    // present was the only candidate content for the
                                    // coming tick - and that content was then permanently
                                    // gone, which is the 90-93% cap it measured. This gate
                                    // asks "is there already an unconsumed present sitting
                                    // in croppedTexture?" instead. The !croppedDirty branch
                                    // has no timer in it at all, and EncodeScheduledFrame
                                    // clears croppedDirty on every tick that consumes
                                    // content, so the first present after each tick is
                                    // always copied immediately. A skip can therefore only
                                    // happen when a newer-or-equal present is already
                                    // pending and guaranteed to be consumed next tick - no
                                    // unique frame is ever lost. When the source presents
                                    // no faster than the target (any V-Sync-on game at a
                                    // matching target), croppedDirty is false on virtually
                                    // every arrival and this is bit-identical to copying
                                    // unconditionally.
                                    //
                                    // The cost is freshness, not content: the pixels a tick
                                    // converts can be up to one target interval (16.7ms at
                                    // 60fps) older than the newest present. That is bounded
                                    // and already measured - watch avgFrameStalenessMs.
                                    //
                                    // The sampling budget preserves a full interval of
                                    // credit and allows one short burst. The refresh was
                                    // close to free while this block was a
                                    // staging copy and the expensive scale/convert Blt was
                                    // deferred to the encode tick. It is not free now that
                                    // the Blt happens HERE: at half an interval a
                                    // fast-presenting source (a browser scrolling, a video
                                    // playing) ran ~87 scale passes a second against a 60fps
                                    // encode, and the extra ~27 were overwritten before
                                    // anything read them. Measured on a 4K desktop scaled to
                                    // 1080p60, that was the difference between ~11% and ~16%
                                    // of the GPU's 3D engine spent on frames nobody sees.
                                    //
                                    // The !croppedDirty branch is untouched, so the first
                                    // present after every tick is still converted
                                    // immediately and no unique frame is lost.
                                    if (duplicationFrameAcquired || cropSamplingBudget.TryConsume(stopwatch.Elapsed, croppedDirty))
                                    {
                                        stageStopwatch.Restart();
                                        using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                                        {
                                            // Preferred path: scale/convert straight out of the
                                            // duplication texture with a source rect, so the crop
                                            // costs nothing of its own. Only valid right here,
                                            // while the frame is still acquired.
                                            // One guard around the whole attempt, not just the
                                            // view creation. The processor was built with the CROP
                                            // as its declared input size and is now handed the full
                                            // desktop texture plus a source rect, which the content
                                            // description is only a hint about - a driver may reject
                                            // the view, the rect, or the Blt itself, and any of
                                            // those throwing here would kill the capture thread.
                                            if (CanUseDirectVideoProcessorInput(
                                                    directBltAvailable,
                                                    frameLease?.RequiresCopyBeforeProcessing == true))
                                            {
                                                try
                                                {
                                                    var desktopKey = desktopTexture.NativePointer;
                                                    if (!desktopInputViews.TryGetValue(desktopKey, out var desktopView))
                                                    {
                                                        desktopView = videoDevice!.CreateVideoProcessorInputView(desktopTexture, vpEnumerator!, new VideoProcessorInputViewDescription
                                                        {
                                                            FourCC = 0,
                                                            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                                                            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
                                                        });
                                                        desktopInputViews[desktopKey] = desktopView;
                                                    }

                                                    videoContext!.VideoProcessorSetStreamSourceRect(
                                                        videoProcessor, 0, true,
                                                        new Vortice.RawRect(cropLeft, cropTop, cropLeft + captureWidth, cropTop + captureHeight));
                                                    bltStreams[0].Enable = true;
                                                    bltStreams[0].InputSurface = desktopView;
                                                    videoContext.VideoProcessorBlt(videoProcessor, outputView, 0, 1, bltStreams);
                                                    nv12Ready = true;
                                                }
                                                catch (Exception error)
                                                {
                                                    // Take the copy for the rest of the session, and
                                                    // clear the stream rect so the deferred Blt reads
                                                    // all of croppedTexture as it always did.
                                                    directBltAvailable = false;
                                                    nv12Ready = false;
                                                    try { videoContext!.VideoProcessorSetStreamSourceRect(videoProcessor, 0, false, null); } catch { /* best effort */ }
                                                    AppLog.Info($"Native capture: direct video-processor crop unavailable ({error.Message}) - falling back to the full-resolution crop copy.");
                                                }
                                            }

                                            if (!nv12Ready)
                                            {
                                                var box = new Vortice.Mathematics.Box(cropLeft, cropTop, 0, cropLeft + captureWidth, cropTop + captureHeight, 1);
                                                device.ImmediateContext.CopySubresourceRegion(croppedTexture, 0, 0, 0, 0, desktopTexture, 0, box);
                                            }
                                        }
                                        if (frameLease?.RequiresCopyBeforeProcessing == true)
                                        {
                                            // Signal transport release directly behind the
                                            // copy. Cursor work, scaling, readback and encode
                                            // now use processing-owned resources only.
                                            frameLease.Dispose();
                                            frameLease = null;
                                            desktopResource.Dispose();
                                            desktopResource = null;
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
                                        cropCopies++;
                                        contentAdvanced = true;
                                    }
                                    else
                                    {
                                        cropCopiesSkipped++;
                                    }
                                }
                                else
                                {
                                    PrepareSoftwareFrameForWrite();
                                    stageStopwatch.Restart();
                                    using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                                    {
                                        var box = new Vortice.Mathematics.Box(cropLeft, cropTop, 0, cropLeft + captureWidth, cropTop + captureHeight, 1);
                                        device.ImmediateContext.CopySubresourceRegion(staging, 0, 0, 0, 0, desktopTexture, 0, box);
                                    }
                                    if (frameLease?.RequiresCopyBeforeProcessing == true)
                                    {
                                        frameLease.Dispose();
                                        frameLease = null;
                                        desktopResource.Dispose();
                                        desktopResource = null;
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
                                        // Reused, same reason as the VideoProcessorStream
                                        // array on the GPU path: these ran once per
                                        // present, not per encoded frame.
                                        swsSrcData[0] = (byte*)mapped.DataPointer;
                                        swsSrcStride[0] = (int)mapped.RowPitch;
                                        ffmpeg.sws_scale(swsContext, swsSrcData, swsSrcStride, 0, captureHeight, frame->data, frame->linesize);
                                        TryOfferDetectorSoftwareFrame(frame->data[0], frame->linesize[0]);
                                    }
                                    finally
                                    {
                                        device.ImmediateContext.Unmap(staging, 0);
                                    }
                                    scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;
                                    processingLatency.Record(stageStopwatch.Elapsed);
                                    RetainAmfSoftwareFrame();
                                    contentAdvanced = true;
                                }

                                // Real-world moment this content actually landed in frame->data -
                                // see the pacing gate below (frameStalenessMs) for why: at a source
                                // fps well above target (240 ->60 here), whatever's newest in
                                // frame->data when the pacing gate fires can still be up to one
                                // source-interval stale, varying iteration to iteration since the
                                // two clocks (source presents, target-fps pacing) run free-running
                                // and unsynchronized - a plausible source of visible judder despite
                                // every output frame being unique and perfectly PTS-spaced.
                                // Only advanced when this present's pixels actually
                                // went somewhere: a present skipped by the crop-copy
                                // gate leaves croppedTexture holding slightly older
                                // content, and claiming otherwise here would hide
                                // exactly the staleness this metric exists to bound.
                                if (contentAdvanced)
                                {
                                    lastFrameContentCapturedUtc = MonotonicClock.UtcNow;
                                    framesProcessedSinceLog++;
                                }
                                // A pointer-only update changes captured pixels after
                                // composition, but does not prove desktop content moved.
                                if (hasDesktopContentUpdate)
                                    Volatile.Write(ref _lastRealContentTicks, MonotonicClock.UtcNow.Ticks);
                                // Set on both the GPU-scale and CPU-copy paths, unlike
                                // croppedDirty which only the GPU one uses - the pacing
                                // gate needs to know "is the next scheduled frame a pad"
                                // regardless of which path produced the content.
                                // Also set on a skipped crop copy, which is correct: a
                                // skip only happens when croppedTexture already holds
                                // unconsumed content, so the next tick is not a pad.
                                Volatile.Write(ref freshContentSinceLastEncode, 1);
                            }
                            // else: occluded - frame->data still holds the last successfully
                            // scaled content, re-encoded unchanged below (visual freeze).
                        }
                        finally
                        {
                            Monitor.Exit(gpuLock);
                            if (duplicationFrameAcquired) duplication?.ReleaseFrame();
                            desktopResource?.Dispose();
                            frameLease?.Dispose();
                        }
                    }
                }
                else
                {
                    if (duplicationFrameAcquired) duplication?.ReleaseFrame();
                    desktopResource?.Dispose();
                    frameLease?.Dispose();
                    if (!usingWgc && acquireResultCode == ResultCode.AccessLost.Code)
                    {
                        AppLog.Info("Native capture: DXGI duplication access lost, recreating.");
                        duplication?.Dispose();
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
                    else if (!usingWgc && acquireResultCode != ResultCode.WaitTimeout.Code)
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
                            AppLog.Info($"Native capture: AcquireNextFrame failed with 0x{acquireResultCode:X8} ({consecutiveAcquireFailures} in a row).");
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
                // A quiet WGC source is expected for a covered/minimized game.
                // Preserve its session and the output cadence; callback failures
                // explicitly trigger the DXGI fallback above.
                if (wgcCapture is null && dxgiCapture is null && hasCapturedRealFrame && !occluded &&
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
                    // Serialized against the pacing thread for the whole recovery.
                    // The per-frame capture body above releases gpuLock at its Monitor.Exit
                    // before reaching here, and this block never took it - so the device,
                    // duplication, staging/cropped textures, video processor, hwFramesRef
                    // and lastHardwareFrame were all being disposed and freed while the
                    // pacing thread was inside EncodeScheduledFrame* using exactly those
                    // objects (av_hwframe_get_buffer, av_frame_clone, ImmediateContext.Map).
                    //
                    // Deadlock-free: gpuLock is taken only by this capture thread and the
                    // pacing thread. The encoder swap below waits on the ENCODE thread,
                    // which never takes gpuLock, so holding it across that wait cannot
                    // deadlock - the pacing thread simply blocks until recovery finishes.
                    // Recovery only runs after a multi-second stall, so the cost of
                    // holding it here is not on any hot path.
                    Monitor.Enter(gpuLock);
                    try
                    {
                    try
                    {
                        if (rebuildDevice)
                        {
                            // Release the duplication we already hold BEFORE
                            // asking DXGI for another one on the same output.
                            // DuplicateOutput answers E_INVALIDARG when this
                            // process is already duplicating that output, so
                            // building the replacement first - which the
                            // duplication-only branch below deliberately does
                            // not do - could never succeed. That is the loop in
                            // the logs: 40+ consecutive attempts, each one
                            // standing up a fresh D3D11 device, being refused
                            // the duplication, tearing the device back down and
                            // trying again two seconds later, for over half an
                            // hour. Losing the old duplication early costs
                            // nothing here; it has already stopped producing
                            // frames, which is the entire reason for recovering.
                            duplication?.Dispose();
                            duplication = null;

                            var newDevice = CreateD3D11Device(out processingGpuPriority);
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

                            staging?.Dispose();
                            // Keyed by texture pointer and owned by the device
                            // going away here - a stale entry would hand the
                            // rebuilt pipeline a view over a dead resource.
                            foreach (var view in desktopInputViews.Values) view.Dispose();
                            desktopInputViews.Clear();
                            nv12Ready = false;
                            inputView?.Dispose();
                            croppedTexture?.Dispose();
                            cursorInputView?.Dispose();
                            cursorTexture?.Dispose();
                            outputView?.Dispose();
                            if (nv12StagingRing is not null) foreach (var t in nv12StagingRing) t.Dispose();
                            if (detectorStagingRing is not null) foreach (var t in detectorStagingRing) t.Dispose();
                            nv12Output?.Dispose();
                            videoProcessor?.Dispose();
                            vpEnumerator?.Dispose();
                            videoContext?.Dispose();
                            videoDevice?.Dispose();
                            device.Dispose();

                            inputView = null;
                            croppedTexture = null;
                            cursorInputView = null;
                            cursorTexture = null;
                            gpuCursorAvailable = false;
                            outputView = null;
                            nv12StagingRing = null;
                            detectorStagingRing = null;
                            detectorStagingIndex = 0;
                            detectorRingWritten = 0;
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
                                // No CaptureCursor exclusion here any more: the cursor
                                // is composited into NV12 after the scale (see
                                // DrawDesktopCursorNv12), so it no longer requires the
                                // CPU path. This copy of the old restriction outlived
                                // the initial-setup one and quietly downgraded every
                                // cursor-enabled capture to the 10-13ms/frame
                                // sws_scale path the moment a stall triggered a device
                                // rebuild.
                                (videoDevice, videoContext, vpEnumerator, videoProcessor, nv12Output, nv12StagingRing, outputView) =
                                    CreateGpuScaler(device, captureWidth, captureHeight, outputWidth, outputHeight, config.FrameRate);
                                detectorStagingRing =
                                [
                                    CreateNv12StagingTexture(device, outputWidth, outputHeight),
                                    CreateNv12StagingTexture(device, outputWidth, outputHeight),
                                    CreateNv12StagingTexture(device, outputWidth, outputHeight)
                                ];
                                (croppedTexture, inputView) = CreateGpuCropInputView(device, videoDevice, vpEnumerator, captureWidth, captureHeight);
                                if (config.CaptureCursor)
                                {
                                    (cursorTexture, cursorInputView) = CreateGpuCursorOverlay(device, videoDevice, vpEnumerator);
                                    gpuCursorAvailable = true;
                                }
                                useGpuScale = true;
                            }
                            catch (Exception error)
                            {
                                AppLog.Info($"Native capture: GPU downscale unavailable after device rebuild, falling back to CPU scale: {error.Message}");
                                useGpuScale = false;
                            }
                            // hw_frames_ctx binds the D3D11 pool for an encoder's
                            // entire lifetime. Rebind only to the same D3D11 frame
                            // type; an NV12 fallback here races queued D3D11 frames
                            // and previously crashed the worker with 0xC0000005.
                            if (hardwareFramesActive)
                            {
                                if (lastHardwareFrame is not null) { var staleHardwareFrame = lastHardwareFrame; ffmpeg.av_frame_free(&staleHardwareFrame); lastHardwareFrame = null; }
                                hardwarePoolTextures.Clear();
                                ReleaseHardwareFrames(ref hwDeviceRef, ref hwFramesRef);

                                if (useGpuScale && (!config.CaptureCursor || gpuCursorAvailable))
                                {
                                    (hwDeviceRef, hwFramesRef) = TryCreateD3D11EncodeFrames(
                                    device, outputWidth, outputHeight, ReplayEncoderProfilePolicy.D3D11FixedPoolSize(config.FrameRate, HardwareFramePoolHeadroom));
                                }

                                AVCodecContext* replacement = null;
                                string rebuiltEncoderName = string.Empty;
                                var rebuiltHardware = false;
                                try
                                {
                                    if (hwFramesRef != 0)
                                    {
                                        replacement = CreateEncoder(config, outputWidth, outputHeight, hwFramesRef, device,
                                            out _, out rebuiltEncoderName, out rebuiltHardware,
                                            candidateOrder: new[] { activeEncoderCandidate });
                                    }
                                }
                                catch (Exception error)
                                {
                                    AppLog.Info($"Native capture: D3D11 encoder rebind unavailable ({error.Message}).");
                                }

                                if (ReplayEncoderFailoverPolicy.RequiresWorkerRestartAfterDeviceRebind(
                                        activeEncoderCandidate, rebuiltHardware))
                                {
                                    if (replacement is not null) ffmpeg.avcodec_free_context(&replacement);
                                    ReleaseHardwareFrames(ref hwDeviceRef, ref hwFramesRef);
                                    SetHealth(_health with
                                    {
                                        State = ReplayCaptureState.Degraded,
                                        LastFailure = "D3D11 encoder could not rebind after device recovery; restarting capture worker.",
                                        PipelineRecoveryAction = ReplayPipelineRecoveryAction.RestartWorker,
                                        UpdatedUtc = DateTime.UtcNow
                                    });
                                    AppLog.Info("Native capture: D3D11 encoder rebind failed; requested supervised worker restart.");
                                    return;
                                }

                                var swapped = new ManualResetEventSlim(false);
                                lock (encodeQueueGate)
                                {
                                    encodeQueue!.Add(new EncodeJob(0, DateTime.UtcNow, (nint)replacement, swapped));
                                }
                                // Bounded: if the encode thread were wedged, waiting
                                // forever here would take the capture thread down with
                                // it. The old context is retired either way - it is
                                // only freed after the thread has joined.
                                if (!swapped.Wait(TimeSpan.FromSeconds(5)))
                                {
                                    AppLog.Error("Native capture: encoder swap after device rebuild did not complete in 5s.", new TimeoutException());
                                }

                                swapped.Dispose();
                                retiredCodecContexts.Add((nint)codecContext);
                                codecContext = replacement;
                                hardwareFramesActive = true;
                                requiresDistinctAmfSoftwareFrame = false;
                                AppLog.Info($"Native capture: encoder rebound after device rebuild ({rebuiltEncoderName}, zeroCopy={rebuiltHardware}).");
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
                        recoveryRetryInterval = baseRecoveryRetryInterval;
                    }
                    catch (Exception error)
                    {
                        var nextInterval = TimeSpan.FromTicks(Math.Min(recoveryRetryInterval.Ticks * 2, maxRecoveryRetryInterval.Ticks));
                        AppLog.Error($"Native capture: stall recovery failed (attempt {recoveryAttempts}, rebuildDevice={rebuildDevice}, retryInSeconds={nextInterval.TotalSeconds:0.#}).", error);
                        recoveryRetryInterval = nextInterval;

                        // Do not loop forever on a duplication that Windows has
                        // refused repeatedly. WGC is intentionally a one-way
                        // recovery source for this target session; a target change
                        // or capture restart is the only route back to DXGI.
                        if (!isMonitorMode && wgcCapture is null && recoveryAttempts >= recoveryAttemptsBeforeDeviceRebuild)
                        {
                            try
                            {
                                wgcCapture = WindowGraphicsCaptureSource.Create(device, gpuLock, targetHandle, config.CaptureCursor, activeFrameRate);
                                activeGameFrameSource = wgcCapture;
                                var size = wgcCapture.ContentSize;
                                desktopBounds = new Vortice.RawRect(0, 0, size.Width, size.Height);
                                duplication?.Dispose();
                                duplication = null;
                                AppLog.Info("Native capture: DXGI recovery exhausted; switched to bounded WGC for this target session.");
                            }
                            catch (Exception wgcError)
                            {
                                AppLog.Error("Native capture: WGC recovery source could not start.", wgcError);
                            }
                        }
                    }
                    }
                    finally
                    {
                        Monitor.Exit(gpuLock);
                    }

                    // The recreate above can leave `duplication` null on failure;
                    // the null-guard at the top of the loop retries it, and
                    // dereferencing it below would crash the whole session.
                    if (wgcCapture is null && duplication is null) continue;
                }

                if (occluded != isPaused)
                {
                    isPaused = occluded;
                    lock (_bufferLock) _pauseEvents.Add(new PauseEvent(MonotonicClock.UtcNow, isPaused));
                    AppLog.Info($"Native capture: recording {(isPaused ? "paused (window not foreground)" : "resumed")}.");
                }

                if (!occluded && hasDesktopContentUpdate && !hasCapturedRealFrame)
                {
                    // GPU scaling normally defers the NV12 readback until the
                    // pacing tick. Qualification needs the actual foreground
                    // pixels for both hardware and system-memory trials, so
                    // materialize this first frame before opening trial contexts.
                    if (useGpuScale && croppedDirty && nv12StagingRing is not null)
                    {
                        lock (gpuLock)
                        {
                            if (!nv12Ready)
                            {
                                bltStreams[0].Enable = true;
                                bltStreams[0].InputSurface = inputView;
                                bltStreams[1].Enable = false;
                                videoContext!.VideoProcessorBlt(videoProcessor, outputView, 0, 1, bltStreams);
                            }

                            var qualificationSlot = nv12StagingIndex;
                            device.ImmediateContext.CopyResource(nv12StagingRing[qualificationSlot], nv12Output);
                            var mapResult = device.ImmediateContext.Map(nv12StagingRing[qualificationSlot], 0u, MapMode.Read, MapFlags.None, out var mapped);
                            if (mapResult.Success)
                            {
                                PrepareSoftwareFrameForWrite();
                                CopyNv12PlanesToFrame(mapped, outputWidth, outputHeight, frame);
                                if (cursorOutputX != int.MinValue)
                                    DrawDesktopCursorNv12(frame, outputWidth, outputHeight, cursorOutputX, cursorOutputY);
                                device.ImmediateContext.Unmap(nv12StagingRing[qualificationSlot], 0);
                                lastFrameContentCapturedUtc = MonotonicClock.UtcNow;
                            }
                            nv12Ready = false;
                            croppedDirty = false;
                            nv12StagingIndex = (qualificationSlot + 1) % nv12StagingRing.Length;
                        }
                    }

                    // The provisional context only keeps startup infrastructure
                    // alive. Qualification is deliberately run once, here, from
                    // the first actual foreground frame; no packet can enter the
                    // replay ring before this swap because pacing is gated on
                    // hasCapturedRealFrame.
                    var qualifiedEncoder = CreateEncoder(
                        config, outputWidth, outputHeight, hwFramesRef, device,
                        out var qualifiedTimeBase, out var qualifiedEncoderName,
                        out var qualifiedHardwareFrames,
                        frame,
                        useGpuScale ? nv12Output : null);
                    if (qualifiedEncoder is null)
                        throw new InvalidOperationException("Foreground encoder qualification did not produce a context.");

                    if (codecContext is not null)
                    {
                        var provisional = codecContext;
                        ffmpeg.avcodec_free_context(&provisional);
                    }
                    codecContext = qualifiedEncoder;
                    _timeBase = qualifiedTimeBase;
                    _videoCodecId = codecContext->codec_id;
                    hardwareFramesActive = qualifiedHardwareFrames;
                    encoderName = qualifiedEncoderName;
                    activeEncoderCandidate = ResolveEncoderCandidate(config, encoderName, qualifiedHardwareFrames);
                    attemptedEncoderCandidates.Add(activeEncoderCandidate);
                    requiresDistinctAmfSoftwareFrame = !qualifiedHardwareFrames && qualifiedEncoderName.Contains("amf", StringComparison.OrdinalIgnoreCase);
                    if (!qualifiedHardwareFrames) ReleaseHardwareFrames(ref hwDeviceRef, ref hwFramesRef);
                    if (codecContext is not null && InitFullSessionWriter(config, codecContext, out fullSessionFormatContext, out fullSessionStream, out fullSessionTempVideoPath, out fullSessionFinalOutputPath))
                    {
                        fullSessionStartUtc = MonotonicClock.UtcNow;
                        fullSessionStartWallUtc = DateTime.UtcNow;
                        fullSessionGameDisplayName = config.GameDisplayName;
                    }

                    packet = ffmpeg.av_packet_alloc();
                    if (packet is null) throw new InvalidOperationException("Native replay could not allocate its encoder packet.");
                    var encodeCodecContextPtr = (nint)codecContext;
                    var encodePacketPtr = (nint)packet;
                    var encodeFullSessionFormatContextPtr = (nint)fullSessionFormatContext;
                    var encodeFullSessionStreamPtr = (nint)fullSessionStream;
                    encodeThread = new Thread(() => EncodeLoop(encodeQueue!, encodeCodecContextPtr, encodePacketPtr, encodeFullSessionFormatContextPtr, encodeFullSessionStreamPtr, submissionLatency, outputLatency))
                    {
                        IsBackground = true,
                        Name = "ClypDat-NativeEncode"
                    };
                    try { encodeThread.Priority = ThreadPriority.AboveNormal; }
                    catch (Exception error) { AppLog.Error("Native capture: failed to raise encode thread priority (non-fatal)", error); }
                    encodeThread.Start();
                    AppLog.Info($"Native replay: foreground encoder opened {qualifiedEncoderName} ({(qualifiedHardwareFrames ? "D3D11" : "system-memory")}); replay timeline starts at zero.");
                    SetHealth(_health with
                    {
                        State = ReplayCaptureState.Healthy,
                        Encoder = qualifiedEncoderName,
                        EncodeQueueCapacity = encodeQueueCapacity,
                        StartupPhase = ReplayCaptureStartupPhase.Ready,
                        UpdatedUtc = DateTime.UtcNow
                    });

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

                // session as real recorded content).

            }

            // The pacing tick, running on its own thread (see pacingThread below).
            // Everything in here used to be the tail of the acquire loop above,
            // which is what forced acquireTimeoutMs to stay under one frame
            // interval: the tick could never be later than the acquire wait. That
            // cap is the single biggest cost of this backend - Windows bills GPU
            // engine time per AcquireNextFrame CALL, and the cap kept the call
            // count high (see the measurements at acquireTimeoutMs). Split apart,
            // the acquire wait is free to be long and the tick keeps its own time.
            // Shared tail of both pacing modes below: force a keyframe on
            // schedule, clone frame (already carrying whatever pts/pict_type
            // the caller just set) and hand it to EncodeLoop. Factored out
            // since the fixed-rate and adaptive-rate branches only differ in
            // WHEN/how they decide to call this, not in what encoding a
            // scheduled frame actually does.
            // Enqueue the latest capture work without letting a temporary
            // encoder stall turn into visible, seconds-old video. Control jobs
            // are never evicted: they switch encoders after a device rebuild.
            bool QueueLatestFrame(nint framePointer)
            {
                var queue = encodeQueue!;
                var job = new EncodeJob(framePointer, MonotonicClock.UtcNow);
                lock (encodeQueueGate)
                {
                    if (queue.IsAddingCompleted)
                    {
                        FreeEncodeFrame(framePointer);
                        return false;
                    }

                    if (queue.TryAdd(job)) return true;

                    if (queue.TryTake(out var stale))
                    {
                        if (stale.FramePtr == 0)
                        {
                            // A pending device-rebuild switch must remain in
                            // order ahead of normal video work.
                            queue.Add(stale);
                        }
                        else
                        {
                            FreeEncodeFrame(stale.FramePtr);
                            Interlocked.Increment(ref _encodeDroppedCount);
                            Interlocked.Increment(ref _totalDroppedFrames);
                            Interlocked.Increment(ref encodeQueueReplacements);
                            if (queue.TryAdd(job)) return true;
                        }
                    }
                }

                FreeEncodeFrame(framePointer);
                Interlocked.Increment(ref _encodeDroppedCount);
                Interlocked.Increment(ref _totalDroppedFrames);
                return false;
            }

            // Zero-copy twin of EncodeScheduledFrame below: the scaled NV12
            // surface goes straight into one of the encoder's own D3D11
            // pool textures and that texture is what gets queued. No
            // staging copy, no Map, no plane memcpy, and nothing for
            // ffmpeg to upload again on the other side - see
            // TryCreateD3D11EncodeFrames for the measurements that motivate
            // it. `frame` still carries the pts/pict_type the pacing loop
            // just set; it is only ever a carrier on this path.
            unsafe void EncodeScheduledFrameHardware()
            {
                if (croppedDirty)
                {
                    stageStopwatch.Restart();
                    // A pool frame and its Vortice wrapper have no ordering
                    // requirement with acquisition. Keep their allocation out of
                    // gpuLock; only D3D11 Video Processor and copy commands need
                    // serialization with frame acquisition.
                    AVFrame* pooled = ffmpeg.av_frame_alloc();
                    AVBufferRef* poolReference = null;
                    ID3D11Device? frameDevice = null;
                    if (pooled is null)
                    {
                        Interlocked.Increment(ref _encodeDroppedCount);
                        Interlocked.Increment(ref _totalDroppedFrames);
                        scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;
                        processingLatency.Record(stageStopwatch.Elapsed);
                        return;
                    }

                    try
                    {
                        lock (gpuLock)
                        {
                            if (!hardwareFramesActive || hwFramesRef == 0 || device is null) return;
                            poolReference = ffmpeg.av_buffer_ref((AVBufferRef*)hwFramesRef);
                            frameDevice = device.QueryInterface<ID3D11Device>();
                        }
                        if (poolReference is null || frameDevice is null ||
                            ffmpeg.av_hwframe_get_buffer(poolReference, pooled, 0) < 0)
                        {
                            Interlocked.Increment(ref _encodeDroppedCount);
                            Interlocked.Increment(ref _totalDroppedFrames);
                            return;
                        }

                        // d3d11 frames carry the texture in data[0] and, since
                        // the pool is one texture ARRAY, the slice index in
                        // data[1] - which is the destination subresource.
                        var texturePointer = (nint)pooled->data[0];
                        var arraySlice = (uint)(nint)pooled->data[1];
                        if (!hardwarePoolTextures.TryGetValue(texturePointer, out var poolTexture))
                        {
                            poolTexture = new ID3D11Texture2D(texturePointer);
                            hardwarePoolTextures[texturePointer] = poolTexture;
                        }

                        lock (gpuLock)
                        {
                            // Device recovery can begin while the pool frame is
                            // allocated. Discard it if its device is no longer
                            // the active processing device.
                            if (!hardwareFramesActive || device is null ||
                                frameDevice.NativePointer != device.NativePointer)
                                return;
                            if (!nv12Ready)
                            {
                                bltStreams[0].Enable = true;
                                bltStreams[0].InputSurface = inputView;
                                if (gpuCursorAvailable && cursorInputView is not null)
                                {
                                    var cursorVisible = cursorOutputX > -12 && cursorOutputY > -15 &&
                                        cursorOutputX < outputWidth && cursorOutputY < outputHeight;
                                    if (cursorVisible)
                                    {
                                        UpdateGpuCursorOverlay(frameDevice, cursorTexture!);
                                        bltStreams[1].Enable = true;
                                        bltStreams[1].InputSurface = cursorInputView;
                                        videoContext!.VideoProcessorSetStreamSourceRect(videoProcessor, 1, true, new Vortice.RawRect(0, 0, 12, 15));
                                        videoContext.VideoProcessorSetStreamDestRect(videoProcessor, 1, true, new Vortice.RawRect(cursorOutputX, cursorOutputY, cursorOutputX + 12, cursorOutputY + 15));
                                        videoContext.VideoProcessorSetStreamAlpha(videoProcessor, 1, true, 1.0f);
                                    }
                                    else bltStreams[1].Enable = false;
                                }
                                else bltStreams[1].Enable = false;
                                videoContext!.VideoProcessorBlt(videoProcessor, outputView, 0, 2, bltStreams);
                            }

                            TryOfferDetectorGpuFrameUnderLock();

                            nv12Ready = false;
                            croppedDirty = false;
                            frameDevice.ImmediateContext.CopySubresourceRegion(poolTexture, arraySlice, 0, 0, 0, nv12Output!, 0);
                        }

                        if (lastHardwareFrame is not null) { var staleHardwareFrame = lastHardwareFrame; ffmpeg.av_frame_free(&staleHardwareFrame); lastHardwareFrame = null; }
                        lastHardwareFrame = pooled;
                        pooled = null;
                    }
                    finally
                    {
                        if (poolReference is not null) ffmpeg.av_buffer_unref(&poolReference);
                        frameDevice?.Dispose();
                        if (pooled is not null) ffmpeg.av_frame_free(&pooled);
                        scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;
                        processingLatency.Record(stageStopwatch.Elapsed);
                    }
                }

                // Nothing captured yet at all - no texture to send, and the
                // system-memory path's black placeholder has no equivalent
                // here. The pacing loop already advanced its timeline, so
                // this reads as a dropped frame, not a slower clip.
                if (lastHardwareFrame is null) return;

                if (stopwatch.Elapsed - lastForcedKeyframe >= TimeSpan.FromSeconds(2) ||
                    Interlocked.Exchange(ref _forceKeyframeRequested, 0) == 1)
                {
                    lastHardwareFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                    lastForcedKeyframe = stopwatch.Elapsed;
                }
                else
                {
                    lastHardwareFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                }

                var staleness = (MonotonicClock.UtcNow - lastFrameContentCapturedUtc).TotalMilliseconds;
                frameStalenessMs += staleness;
                if (staleness > frameStalenessMaxMs) frameStalenessMaxMs = staleness;
                frameStalenessCount++;

                stageStopwatch.Restart();
                // Cloned, not handed over: a padding tick sends this same
                // surface again, so the capture side has to keep its own
                // reference. The clone is a ref-counted handle to the very
                // same texture, not a copy of it.
                lastHardwareFrame->pts = frame->pts;
                var outgoing = ffmpeg.av_frame_clone(lastHardwareFrame);
                if (outgoing is null)
                {
                    Interlocked.Increment(ref _encodeDroppedCount);
                    Interlocked.Increment(ref _totalDroppedFrames);
                }
                else if (QueueLatestFrame((nint)outgoing))
                {
                    framesEncoded++;
                    framesEncodedSinceLog++;
                }

                encodeMs += stageStopwatch.Elapsed.TotalMilliseconds;
            }

            unsafe void EncodeScheduledFrame()
            {
                bool encodeHardwareFrame;
                lock (gpuLock)
                    encodeHardwareFrame = hardwareFramesActive && hwFramesRef != 0;
                if (encodeHardwareFrame)
                {
                    EncodeScheduledFrameHardware();
                    return;
                }

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
                    // Reused rather than allocated per frame - 60/sec of these
                    // into a loop whose own diagnostics already blame blocking
                    // GCs for multi-hundred-millisecond capture gaps. inputView
                    // is refreshed in place because a crop resize or device
                    // rebuild replaces the view object underneath us.
                    // Already produced at present time straight off the
                    // duplication texture - see directBltAvailable. Only the
                    // fallback copy path still owes a Blt here.
                    // As on the hardware path: the lock covers the D3D work only.
                    int ringLength, currentRingIndex;
                    lock (gpuLock)
                    {
                        if (!nv12Ready)
                        {
                            bltStreams[0].Enable = true;
                            bltStreams[0].InputSurface = inputView;
                            videoContext!.VideoProcessorBlt(videoProcessor, outputView, 0, 1, bltStreams);
                        }

                        TryOfferDetectorGpuFrameUnderLock();

                        nv12Ready = false;
                        ringLength = nv12StagingRing!.Length;
                        currentRingIndex = nv12StagingIndex;
                        device.ImmediateContext.CopyResource(nv12StagingRing[currentRingIndex], nv12Output);
                        if (nv12RingWritten < ringLength) nv12RingWritten++;
                    }
                    copyMapMs += stageStopwatch.Elapsed.TotalMilliseconds;

                    stageStopwatch.Restart();
                    // k=1 is the slot written on the previous tick (freshest of the
                    // finished ones), counting back to the oldest still held.
                    for (var k = 1; k < nv12RingWritten; k++)
                    {
                        var candidate = ((currentRingIndex - k) % ringLength + ringLength) % ringLength;
                        Monitor.Enter(gpuLock);
                        var mapResult = device.ImmediateContext.Map(
                            nv12StagingRing[candidate], 0u, MapMode.Read, MapFlags.DoNotWait, out var mapped);
                        // DXGI_ERROR_WAS_STILL_DRAWING - this slot's copy has not
                        // landed yet, so try an older one rather than wait on it.
                        if (mapResult.Failure)
                        {
                            Monitor.Exit(gpuLock);
                            continue;
                        }

                        // The lock has to span the whole map/copy/unmap - the
                        // mapped pointer is only valid while it is held.
                        try
                        {
                            PrepareSoftwareFrameForWrite();
                            CopyNv12PlanesToFrame(mapped, outputWidth, outputHeight, frame);
                            if (cursorOutputX != int.MinValue)
                            {
                                DrawDesktopCursorNv12(frame, outputWidth, outputHeight, cursorOutputX, cursorOutputY);
                            }
                        }
                        finally
                        {
                            device.ImmediateContext.Unmap(nv12StagingRing[candidate], 0);
                            Monitor.Exit(gpuLock);
                        }

                        RetainAmfSoftwareFrame();
                        break;
                    }
                    scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;
                    processingLatency.Record(stageStopwatch.Elapsed);

                    nv12StagingIndex = (currentRingIndex + 1) % ringLength;
                    croppedDirty = false;
                }

                // Force a keyframe periodically so the ring buffer always has a nearby
                // point to start a save-window at without waiting on the encoder's own
                // GOP schedule. An encoder swap also asks for one immediately, so the
                // new generation has a cut point right at its boundary.
                if (stopwatch.Elapsed - lastForcedKeyframe >= TimeSpan.FromSeconds(2) ||
                    Interlocked.Exchange(ref _forceKeyframeRequested, 0) == 1)
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
                // pixel copy. AMF's system-memory path additionally keeps a
                // guard reference so the next real capture always gets a
                // fresh backing buffer before AMF can cache the old pointer.
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
                else if (QueueLatestFrame((nint)clonedFrame))
                {
                    // PTS advances above once per scheduled frame. It must not
                    // advance again here: doing both doubles every successful
                    // frame's duration and turns a 60 FPS clip into ~30 FPS.
                    framesEncoded++;
                    framesEncodedSinceLog++;
                }
                encodeMs += stageStopwatch.Elapsed.TotalMilliseconds;
            }

            void PrepareSoftwareFrameForWrite()
            {
                if (ffmpeg.av_frame_make_writable(frame) < 0)
                {
                    AppLog.Error("Native capture: failed to make software frame writable.");
                    return;
                }

                if (amfSoftwareFrameGuard is not null)
                {
                    var staleGuard = amfSoftwareFrameGuard;
                    amfSoftwareFrameGuard = null;
                    ffmpeg.av_frame_free(&staleGuard);
                }
            }

            void RetainAmfSoftwareFrame()
            {
                if (!requiresDistinctAmfSoftwareFrame) return;
                var retained = ffmpeg.av_frame_clone(frame);
                if (retained is null)
                {
                    AppLog.Error("Native capture: failed to retain AMF software frame backing.");
                    return;
                }

                if (amfSoftwareFrameGuard is not null)
                {
                    var staleGuard = amfSoftwareFrameGuard;
                    ffmpeg.av_frame_free(&staleGuard);
                }
                amfSoftwareFrameGuard = retained;
            }

            unsafe void TryOfferDetectorGpuFrameUnderLock()
            {
                if (DetectorFrameAvailable is null || detectorStagingRing is null || nv12Output is null) return;
                var now = stopwatch.Elapsed;
                if (lastDetectorSample != TimeSpan.MinValue && now - lastDetectorSample < TimeSpan.FromMilliseconds(500)) return;
                lastDetectorSample = now;

                for (var age = 1; age <= detectorRingWritten; age++)
                {
                    var candidate = ((detectorStagingIndex - age) % detectorStagingRing.Length + detectorStagingRing.Length) % detectorStagingRing.Length;
                    var result = device.ImmediateContext.Map(detectorStagingRing[candidate], 0u, MapMode.Read, MapFlags.DoNotWait, out var mapped);
                    if (result.Failure) continue;
                    try { OfferDetectorFrame((byte*)mapped.DataPointer, (int)mapped.RowPitch); }
                    finally { device.ImmediateContext.Unmap(detectorStagingRing[candidate], 0); }
                    break;
                }

                device.ImmediateContext.CopyResource(detectorStagingRing[detectorStagingIndex], nv12Output);
                detectorStagingIndex = (detectorStagingIndex + 1) % detectorStagingRing.Length;
                if (detectorRingWritten < detectorStagingRing.Length) detectorRingWritten++;
            }

            unsafe void TryOfferDetectorSoftwareFrame(byte* luminance, int rowPitch)
            {
                if (DetectorFrameAvailable is null) return;
                var now = stopwatch.Elapsed;
                if (lastDetectorSample != TimeSpan.MinValue && now - lastDetectorSample < TimeSpan.FromMilliseconds(500)) return;
                lastDetectorSample = now;
                OfferDetectorFrame(luminance, rowPitch);
            }

            unsafe void OfferDetectorFrame(byte* luminance, int rowPitch)
            {
                // Initial detector pack is deliberately fail-closed outside
                // standard 16:9 SDR layouts.
                if (!IsSupportedDetectorAspectRatio(outputWidth, outputHeight)) return;
                DetectorFrameAvailable?.Invoke(this, new DetectorFrameSnapshot(
                    MonotonicClock.UtcNow,
                    CropGray(luminance, rowPitch, new NormalizedRegion(0.34, 0.445, 0.32, 0.065)),
                    CropGray(luminance, rowPitch, new NormalizedRegion(0.42, 0.335, 0.16, 0.055)),
                    CropGray(luminance, rowPitch, new NormalizedRegion(0.45, 0.72, 0.12, 0.12))));
            }

            unsafe GrayDetectorImage CropGray(byte* luminance, int rowPitch, NormalizedRegion region)
            {
                var rect = region.ToPixelRect(outputWidth, outputHeight);
                var pixels = new byte[rect.Width * rect.Height];
                for (var row = 0; row < rect.Height; row++)
                {
                    var source = new ReadOnlySpan<byte>(luminance + (rect.Y + row) * rowPitch + rect.X, rect.Width);
                    source.CopyTo(pixels.AsSpan(row * rect.Width, rect.Width));
                }
                return new GrayDetectorImage(rect.Width, rect.Height, pixels);
            }

            // Pacing/encode gate - runs on its own clock regardless of whether
            // the acquire loop produced fresh content, rather than only inside
            // the successful-real-frame branch it used to live in. Previously the
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
            void RunPacingTick()
            {
                if (hasCapturedRealFrame && variableFrameTiming)
                {
                    var now = stopwatch.Elapsed;
                    var hasFreshContent = Interlocked.Exchange(ref freshContentSinceLastEncode, 0) != 0;
                    // Keep selected-rate cadence through a source gap. Fresh
                    // frames retain their real timestamps; duplicates only fill
                    // the interval where the source supplied nothing new.
                    var dueForTick = ReplayFrameTimingPolicy.TryAdvanceVariableDeadline(
                        now, targetFrameInterval, ref lastEncodedAt);

                    if (dueForTick)
                    {
                        var realPts = ReplayFrameTimingPolicy.RealPtsMicroseconds(now, lastVariablePtsMicroseconds);
                        var gapPts = lastVariablePtsMicroseconds < 0
                            ? realPts
                            : lastVariablePtsMicroseconds + (long)Math.Round(idealFrameIntervalMicroseconds);
                        frame->pts = hasFreshContent ? realPts : gapPts;
                        lastVariablePtsMicroseconds = frame->pts;
                        EncodeScheduledFrame();
                    }
                    else if (hasFreshContent)
                    {
                        // The cap has not reached its next tick yet; retain the
                        // source update for that tick instead of losing it.
                        Volatile.Write(ref freshContentSinceLastEncode, 1);
                    }
                }
                else if (hasCapturedRealFrame)
                {
                if (latestPacing)
                {
                    var now = stopwatch.Elapsed;
                    var intervals = ReplayPacingPolicy.TakeLatestIntervals(now, targetFrameInterval, ref lastEncodedAt);
                    if (intervals > 0)
                    {
                        if (intervals > 1) Interlocked.Add(ref pacingMissedFrames, intervals - 1);
                        frame->pts = (long)Math.Round(nextPtsMicroseconds + idealFrameIntervalMicroseconds * (intervals - 1));
                        nextPtsMicroseconds += idealFrameIntervalMicroseconds * intervals;
                        EncodeScheduledFrame();
                        Volatile.Write(ref freshContentSinceLastEncode, 0);
                    }
                }
                else
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

                    // A pad (nothing new since the last encode) is a byte-identical
                    // copy of the frame before it. Encoding one costs a full pass
                    // through the encoder for zero information, and whenever the
                    // source presents slower than the target it is not a rare event:
                    // a 43fps source into a 60fps target made a quarter of all
                    // encoded frames pads on one measured Iris Xe laptop, where
                    // encode runs on the same execution units the game renders with.
                    // On a backed-up queue it is worse still - the pad also pushes
                    // the real frame behind it further back (the same logs showed
                    // framesEncoded at 2.7x framesSeen with the queue pinned at
                    // 30/30 and output collapsed to 18.8fps).
                    //
                    // Suppressing pads unconditionally was tried and reverted. It
                    // did cut encoder work, but it also made the output rate the
                    // SOURCE's rate rather than the configured one: a game whose
                    // presentation timing is merely uneven (maxPresentGapMs of
                    // 36-41ms against a 16.7ms target, measured on a 4070 Ti at a
                    // steady ~85fps average) leaves real holes in the tick grid,
                    // and a clip that should read 60fps came out at 53.6. Exact
                    // constant frame rate is the requirement; the pads that buys
                    // are cheap on a working encoder (avgEncodeMs 0.46 on NVENC in
                    // the same logs) and the GPU savings came from the crop-copy
                    // limiter and the encoder settings, not from here.
                    //
                    // So this stays what it was: a safety valve, not a policy. The
                    // timeline advances above either way (lastEncodedAt and
                    // nextPtsMicroseconds both moved before this check), so a
                    // skipped pad reads as a dropped frame in a correctly-spaced
                    // timeline rather than a slower clip - the same shape as the
                    // catch-up cap right above, which already decided that under
                    // stall conditions honesty beats padding. Only ever skipped
                    // while the queue is genuinely deep, so steady-state output is
                    // exactly the configured frame rate.
                    // Arms two frames from capacity, not at half depth. Half was
                    // far too eager: a 30-deep queue sits at 15+ routinely during
                    // action - absorbing exactly that is what the queue is FOR -
                    // and every tick spent above it with nothing newly presented
                    // punched a hole in a timeline that is meant to be exactly
                    // the configured rate. Measured at 15-67 pads skipped per 2s
                    // window, which is where a capture logging a clean 120
                    // frames per 2s still saved clips reading 49fps.
                    EncodeScheduledFrame();
                    Volatile.Write(ref freshContentSinceLastEncode, 0);
                }
                }
                }

                if (ReplayPacingPolicy.IsMaintenanceDue(stopwatch.Elapsed, TimeSpan.FromSeconds(1), ref lastRingTrim))
                {
                    // Ring trim rides the pacing tick rather than the acquire
                    // loop now, because the acquire loop can sit in a single
                    // 200ms wait and this wants to run about once a second
                    // regardless of whether the desktop is producing anything.
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

            // Bounded, but generously: RunPacingTick contends on _bufferLock with the
            // encode thread, calls _audio.PruneOlderThan (filesystem deletes), and can
            // encode a catch-up burst in one tick, so 2s was optimistic. Whether it
            // actually stopped decides below whether the native frees are safe to run:
            // the pacing thread touches hwFramesRef, lastHardwareFrame and the D3D
            // device, and its own catch swallows exceptions and keeps looping, so a
            // free underneath it would not even stop at the first bad access.
            pacingThreadStopped = pacingThread.Join(PacingThreadStopTimeout);

            // Stop accepting new jobs and wait for EncodeLoop to drain everything
            // already queued (including its own final flush of whatever's still
            // buffered inside the encoder) - the finally block below also does
            // this on any exception path, so this is a no-op there, not a
            // duplicate drain.
            encodeQueue.CompleteAdding();
            encodeThread?.Join();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stop/restart cancellation is normal lifecycle, not a capture failure.
            ready.TrySetCanceled(token);
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
            foreach (var completion in swapCompletionEvents) completion.Dispose();
            swapCompletionEvents.Clear();
            encodeQueue?.Dispose();

            // Authoritative join. The one after the capture loop covers only the normal
            // exit; an OperationCanceledException from a stop, or a device error, throws
            // from inside the loop and lands here with the thread still running and
            // pacingThreadStopped still false. Skipping the disposals in that case leaked
            // the DXGI duplication, and the next DuplicateOutput on the same output then
            // failed with E_INVALIDARG - which is what a capture restart (changing the
            // encoder, for instance) does immediately afterwards.
            if (pacingThread is not null && !pacingThreadStopped)
            {
                pacingThreadStopped = pacingThread.Join(PacingThreadStopTimeout);
                if (!pacingThreadStopped)
                {
                    AppLog.Error($"Native capture: pacing thread did not stop within {PacingThreadStopTimeout.TotalSeconds:0.#}s; leaking its native resources rather than freeing them underneath it.");
                }
            }

            // Leak rather than free-and-use when the pacing thread is still running: it
            // touches these same pointers, and a leak ends with the process while a
            // use-after-free on libav/D3D pointers is native memory corruption. The
            // full-session finalize below still runs either way - skipping it would
            // lose the recording, which is a worse outcome than a leaked allocation.
            if (pacingThreadStopped)
            {
                if (amfSoftwareFrameGuard is not null) { var staleGuard = amfSoftwareFrameGuard; ffmpeg.av_frame_free(&staleGuard); amfSoftwareFrameGuard = null; }
                if (frame is not null) { var f = frame; ffmpeg.av_frame_free(&f); }
                if (packet is not null) { var p = packet; ffmpeg.av_packet_free(&p); }
                if (swsContext is not null) ffmpeg.sws_freeContext(swsContext);
            }
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
            if (pacingThreadStopped && codecContext is not null) { var c = codecContext; ffmpeg.avcodec_free_context(&c); }
            // Encoders replaced mid-session (device rebuild). Safe only here:
            // the encode thread has already been joined above, so nothing can
            // still be inside one.
            foreach (var retired in retiredCodecContexts)
            {
                var context = (AVCodecContext*)retired;
                ffmpeg.avcodec_free_context(&context);
            }
            retiredCodecContexts.Clear();
            // Frame pool before the textures under it: unreferencing the last
            // frame is what lets the pool release its D3D11 surfaces, and those
            // belong to the device disposed a few lines down.
            // Everything below is touched by the pacing thread's encode path
            // (av_hwframe_get_buffer on hwFramesRef, av_frame_clone of
            // lastHardwareFrame, device.ImmediateContext.Map). Same rule as above:
            // if that thread is still alive, leak instead of freeing under it.
            if (pacingThreadStopped)
            {
                if (lastHardwareFrame is not null) { var staleHardwareFrame = lastHardwareFrame; ffmpeg.av_frame_free(&staleHardwareFrame); lastHardwareFrame = null; }
                // Never disposed, only dropped - these wrappers were built over
                // pointers the pool owns and hold no reference of their own.
                hardwarePoolTextures.Clear();
                ReleaseHardwareFrames(ref hwDeviceRef, ref hwFramesRef);
                wgcCapture?.Dispose();
                dxgiCapture?.Dispose();
                duplication?.Dispose();
                staging?.Dispose();
                foreach (var view in desktopInputViews.Values) view.Dispose();
                desktopInputViews.Clear();
                inputView?.Dispose();
                croppedTexture?.Dispose();
                cursorInputView?.Dispose();
                cursorTexture?.Dispose();
                outputView?.Dispose();
                if (nv12StagingRing is not null) foreach (var t in nv12StagingRing) t.Dispose();
                if (detectorStagingRing is not null) foreach (var t in detectorStagingRing) t.Dispose();
                nv12Output?.Dispose();
                videoProcessor?.Dispose();
                vpEnumerator?.Dispose();
                videoContext?.Dispose();
                videoDevice?.Dispose();
                device?.Dispose();
            }
            if (timerResolutionRaised) TimeEndPeriod(1);
            if (_health.State != ReplayCaptureState.Failed)
            {
                SetHealth(_health with { State = ReplayCaptureState.Stopped, UpdatedUtc = DateTime.UtcNow });
            }
        }
    }

    private static unsafe void FillFrameBlack(AVFrame* frame, int height)
    {
        // Y=16, matching the limited-range NV12 both scalers now produce (see
        // CreateEncoder's AVCOL_RANGE_MPEG). 0 is below-black there and would
        // be a slightly different black from the rest of the recording.
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
            // Lanczos, not bilinear. Measured on a synthetic 2:1 downscale of
            // real capture content, bilinear scores SSIM 0.9952 (23.2dB) against
            // lanczos' 0.9987 (28.8dB) - a 5.6dB detail loss purely from the
            // resampling kernel, which is exactly the "looks like a lower
            // resolution" symptom. ACCURATE_RND drops the rounding shortcuts
            // that would otherwise give some of that back.
            (int)(SwsFlags.SWS_LANCZOS | SwsFlags.SWS_ACCURATE_RND), null, null, null);
        if (swsContext is null) throw new InvalidOperationException("sws_getContext failed.");

        // Desktop Duplication's BGRA capture is full-range (0-255), so the
        // source side is 1; the NV12 output side is 0, studio/limited range
        // (16-235), because that is what H.264 consumers assume when they
        // don't read the VUI flag - see CreateEncoder for why we no longer
        // rely on that flag being honoured. BT.709 coefficients (not swscale's
        // BT.601 default) because that's what the encoder tags the output as,
        // and what any player assumes for HD regardless - converting with 601
        // and being decoded as 709 is a real colour shift.
        var coefficientsPtr = ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
        var coefficients = new int_array4();
        coefficients.UpdateFrom(new[] { coefficientsPtr[0], coefficientsPtr[1], coefficientsPtr[2], coefficientsPtr[3] });
        ffmpeg.sws_setColorspaceDetails(swsContext, in coefficients, 1 /* full source */, in coefficients, 0 /* limited output */, 0, 1 << 16, 1 << 16);

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

    private static (ID3D11Texture2D Texture, ID3D11VideoProcessorInputView InputView) CreateGpuCursorOverlay(
        ID3D11Device device, ID3D11VideoDevice videoDevice, ID3D11VideoProcessorEnumerator enumerator)
    {
        var texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 12,
            Height = 15,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            BindFlags = BindFlags.None
        });

        try
        {
            var inputView = videoDevice.CreateVideoProcessorInputView(texture, enumerator, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
            });
            return (texture, inputView);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private static unsafe void UpdateGpuCursorOverlay(ID3D11Device device, ID3D11Texture2D texture)
    {
        var mapped = device.ImmediateContext.Map(texture, 0, MapMode.WriteDiscard, MapFlags.None);
        try
        {
            var pixels = (byte*)mapped.DataPointer;
            for (var row = 0; row < 15; row++)
            {
                new Span<byte>(pixels + row * (int)mapped.RowPitch, 12 * 4).Clear();
                for (var column = 0; column < 12; column++)
                {
                    var inside = column <= row / 2 || (row > 7 && column >= 3 && column <= 6 && row - 7 <= column - 2);
                    if (!inside) continue;
                    var edge = column == 0 || column == row / 2 || row == 0;
                    var target = pixels + row * (int)mapped.RowPitch + column * 4;
                    target[0] = edge ? (byte)0 : (byte)245;
                    target[1] = edge ? (byte)0 : (byte)245;
                    target[2] = edge ? (byte)0 : (byte)245;
                    target[3] = 255;
                }
            }
        }
        finally
        {
            device.ImmediateContext.Unmap(texture, 0);
        }
    }

    // Sets up the D3D11 Video Processor to do the crop->NV12-downscale that
    // sws_scale otherwise does on the CPU, entirely on the GPU. Only the
    // final small output-resolution NV12 texture ever gets read back to the
    // CPU afterward (via nv12Staging), instead of the full captured crop
    // (often 4K) every single frame. Throws on any failure - caller treats
    // that as "not supported on this hardware/driver" and falls back to CPU
    // scale, so nothing here needs to be defensive beyond that.
    // Enumerator and processor both bake InputWidth/InputHeight into their content
    // description, so a crop-size change invalidates them - the window-resize rebuild
    // used to recreate only the staging texture, the CPU scaler and the crop input view,
    // leaving the processor describing the PREVIOUS input size while a new input view of
    // the new size was blitted through it. Shared by initial creation and that rebuild so
    // the description cannot drift between the two.
    private static (ID3D11VideoProcessorEnumerator Enumerator, ID3D11VideoProcessor Processor) CreateVideoProcessorForSize(
        ID3D11VideoDevice videoDevice, int captureWidth, int captureHeight, int outputWidth, int outputHeight, int frameRate)
    {
        var rate = new Rational((uint)Math.Clamp(frameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate), 1);
        var contentDescription = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)captureWidth,
            InputHeight = (uint)captureHeight,
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            InputFrameRate = rate,
            OutputFrameRate = rate,
            // Capture runs continuously at the target frame rate, unlike a one-off
            // export. OptimalQuality can make the driver spend more 3D time on every
            // crop/scale/colour-conversion pass, competing with the game for the same
            // GPU. The output is immediately fed to NVENC, so throughput matters more
            // than a premium resize kernel here.
            Usage = VideoUsage.OptimalSpeed
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

        try
        {
            return (enumerator, videoDevice.CreateVideoProcessor(enumerator, 0));
        }
        catch (Exception error)
        {
            enumerator.Dispose();
            throw new InvalidOperationException($"CreateVideoProcessor failed: {error.Message}", error);
        }
    }

    private static (ID3D11VideoDevice VideoDevice, ID3D11VideoContext VideoContext, ID3D11VideoProcessorEnumerator Enumerator, ID3D11VideoProcessor Processor, ID3D11Texture2D Nv12Output, ID3D11Texture2D[] Nv12StagingRing, ID3D11VideoProcessorOutputView OutputView)
        CreateGpuScaler(ID3D11Device device, int captureWidth, int captureHeight, int outputWidth, int outputHeight, int frameRate)
    {
        var videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        var videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        var (enumerator, processor) = CreateVideoProcessorForSize(
            videoDevice, captureWidth, captureHeight, outputWidth, outputHeight, frameRate);


        // The two ends of this conversion are deliberately different ranges, so
        // they get their own structs. D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE is
        // UNDEFINED=0, 16_235=1, 0_255=2.
        //
        // Input: Desktop Duplication hands over full-range (0-255) RGB, so the
        // stream colour space says Range_0_255 - anything else has the driver
        // mis-reading the captured pixels before it converts them.
        //
        // Output: limited range (16-235) NV12, matching the CPU sws_scale path
        // in CreateScaler and what CreateEncoder now tags the bitstream as.
        // Both are 1 vs 2 flips away from full-range, which is what this used
        // to be - see CreateEncoder for why signalling full range in the VUI
        // turned out not to be enough to get it read back that way.
        //
        // YCbCr_Matrix=1 is BT.709, matching the encoder's tag rather than the
        // BT.601 default that silently disagreed with how every player reads an
        // HD stream.
        var inputColorSpace = new VideoProcessorColorSpace
        {
            Nominal_Range = (uint)VideoProcessorNominalRange.Range_0_255,
            YCbCr_Matrix = 1
        };
        var outputColorSpace = new VideoProcessorColorSpace
        {
            Nominal_Range = (uint)VideoProcessorNominalRange.Range_16_235,
            YCbCr_Matrix = 1
        };
        videoContext.VideoProcessorSetStreamColorSpace(processor, 0, inputColorSpace);
        videoContext.VideoProcessorSetOutputColorSpace(processor, outputColorSpace);

        // Auto processing is ON by default, and Usage=OptimalSpeed above does
        // NOT turn it off - they are separate switches. Left enabled, the
        // driver is free to run its own enhancement pass on every frame
        // (NVIDIA's does: denoise, edge/detail enhancement, a higher-quality
        // resize kernel), which is exactly the "extra video processing" this
        // call exists to refuse.
        //
        // Measured on an RTX 4070 Ti capturing 3840x2160 -> 1920x1080 at 60fps:
        // ClypDat's own 3D-engine share sat at 8-17% with the window closed to
        // the tray and nothing at all being drawn, while the video-encode
        // engine held flat at ~6%. With the UI proven not to be the source
        // (one InvalidateVisual call site in the whole app, no infinite
        // animations, and hiding the window changed nothing), a per-frame
        // 4K enhancement pass is what is left holding that floor.
        //
        // Frame-rate conversion is refused for the same reason: the encoder
        // wants one output frame per input frame, and letting the driver
        // interpolate toward some other rate is work nobody asked for.
        try
        {
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
            videoContext.VideoProcessorSetStreamOutputRate(
                processor, 0, VideoProcessorOutputRate.Normal, false, null);
        }
        catch (Exception error)
        {
            // Both are hints a driver is allowed to ignore. A driver that
            // rejects them outright still scales correctly, just at whatever
            // cost it prefers - not a reason to fail the capture.
            AppLog.Info($"Native capture: video processor tuning rejected by the driver ({error.Message}) - scaling continues at driver defaults.");
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
        // The two strides do often agree in practice (D3D11's NV12 pitch and
        // ffmpeg's align-32 linesize both land on 1920 at 1080p, for one), and
        // when they do the whole plane is one contiguous block - 1080 separate
        // memcpy calls collapse into one. The per-row path stays for when they
        // don't, which is the only case the original comment above covers.
        if (srcStride == yDstStride)
        {
            Buffer.MemoryCopy(ySrc, yDst, (long)yDstStride * height, (long)srcStride * height);
        }
        else
        {
            for (var row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(ySrc + row * srcStride, yDst + row * yDstStride, yDstStride, width);
            }
        }

        var uvHeight = (height + 1) / 2;
        var uvSrc = ySrc + srcStride * height;
        var uvDst = frame->data[1];
        var uvDstStride = frame->linesize[1];
        if (srcStride == uvDstStride)
        {
            Buffer.MemoryCopy(uvSrc, uvDst, (long)uvDstStride * uvHeight, (long)srcStride * uvHeight);
        }
        else
        {
            for (var row = 0; row < uvHeight; row++)
            {
                Buffer.MemoryCopy(uvSrc + row * srcStride, uvDst + row * uvDstStride, uvDstStride, width);
            }
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

    internal static IDXGIOutputDuplication CreateDuplicationFor(ID3D11Device device, nint targetHandle, ReplayBufferConfig config, out Vortice.RawRect desktopBounds)
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
        return config.GameWindowHandle != 0 && IsWindow((nint)config.GameWindowHandle) ? (nint)config.GameWindowHandle : 0;
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
    // FramePtr == 0 marks a control job: flush the current encoder and continue
    // on SwapCodecContext instead. Sent through the queue rather than applied
    // directly so the switch happens between two frames, with everything
    // already queued encoded on the encoder that produced its timeline - see
    // its use in the D3D device rebuild, which invalidates the D3D11 texture
    // pool the old encoder was reading from.
    internal readonly record struct EncodeJob(
        nint FramePtr,
        DateTime WallClockUtc,
        nint SwapCodecContext = 0,
        ManualResetEventSlim? SwapCompleted = null);

    private static unsafe void FreeEncodeFrame(nint framePointer)
    {
        if (framePointer == 0) return;
        var frame = (AVFrame*)framePointer;
        ffmpeg.av_frame_free(&frame);
    }

    // Runs avcodec_send_frame/receive_packet (and so DrainToRingBuffer, and the
    // full-session mux write inside it) on its own thread, decoupled from
    // CaptureLoop's AcquireNextFrame loop. Existed as a single synchronous call
    // inline in CaptureLoop originally - fine when NVENC keeps up, but a real
    // GPU-contention stall there (confirmed via avgEncodeMs spiking 20x+ baseline
    // with frames backing up hundreds deep in Native capture diag) blocked
    // AcquireNextFrame right along with it, since it was the same thread. This
    // loop owns codecContext/packet/pendingFrameWallClocks exclusively from here
    // on - CaptureLoop never touches them again after starting this thread.
    private unsafe void EncodeLoop(BlockingCollection<EncodeJob> queue, nint codecContextPtr, nint packetPtr, nint fullSessionFormatContextPtr, nint fullSessionStreamPtr, ReplayLatencyHistogram submissionLatency, ReplayLatencyHistogram outputLatency)
    {
        // Same reasoning as the capture loop: this thread owns the encoder, and
        // a stall here backs the queue up until frames start being dropped.
        using var encodeMmcss = MmcssScope.Capture("native encode thread");
        var codecContext = (AVCodecContext*)codecContextPtr;
        var packet = (AVPacket*)packetPtr;
        var fullSessionFormatContext = (AVFormatContext*)fullSessionFormatContextPtr;
        var fullSessionStream = (AVStream*)fullSessionStreamPtr;
        // Some hardware encoders accept a frame before they emit its packet.
        // Keeping only its timestamp meant the reusable capture frame could be
        // overwritten while AMF still read it. Retain frame ownership through
        // packet drain so capture-side writes become copy-on-write.
        var pendingFrames = new EncoderFrameLifetimeQueue(FreeEncodeFrame);
        try
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                if (job.FramePtr == 0)
                {
                    // Control job - see EncodeJob. Flush what the outgoing
                    // encoder still holds into the ring before switching, so no
                    // already-accepted frame is lost across the swap. The old
                    // context is NOT freed here: CaptureLoop owns every context
                    // it created and frees them once this thread has joined,
                    // which is the only point at which nothing can still be
                    // inside one.
                    if (job.SwapCodecContext != 0)
                    {
                        ffmpeg.avcodec_send_frame(codecContext, null);
                        DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream, pendingFrames);
                        pendingFrames.ReleaseAll();
                        Volatile.Write(ref _pendingEncoderFrames, 0);
                        codecContext = (AVCodecContext*)job.SwapCodecContext;
                        // The drain above belongs to the outgoing encoder, so the
                        // generation only advances once the new context is bound.
                        // Everything after this point carries new SPS/PPS that
                        // nothing already in the ring was encoded against.
                        _encoderGeneration++;
                        // Cutting a clip needs a keyframe from the new encoder.
                        // Without asking for one now, the first is up to a full
                        // GOP away and BorrowWindowUnderLock has to discard that
                        // much more history to keep the clip decodable.
                        Interlocked.Exchange(ref _forceKeyframeRequested, 1);
                        if (fullSessionFormatContext is not null)
                        {
                            // The full-session file is one continuous track whose
                            // header was already written from the outgoing
                            // encoder's parameter sets, and it cannot be cut back
                            // the way a replay window can. ClipRepairSweep repairs
                            // it after the fact; say so here so the log explains
                            // the repair when it happens.
                            AppLog.Info("Native capture: encoder replaced mid-session - the full-session recording spans two encoders and will be repaired on the next library scan.");
                        }
                    }

                    job.SwapCompleted?.Set();
                    continue;
                }

                var jobFrame = (AVFrame*)job.FramePtr;
                var accepted = false;
                try
                {
                    var sendTimer = System.Diagnostics.Stopwatch.StartNew();
                    var sendResult = ffmpeg.avcodec_send_frame(codecContext, jobFrame);
                    // FFmpeg's send/receive API requires us to drain output and
                    // retry THIS input frame on EAGAIN.  Freeing it here created
                    // a real hole in the capture timeline whenever a hardware
                    // encoder briefly filled its internal queue.
                    while (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        Interlocked.Increment(ref _sendRefusedEagainCount);
                        DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream, pendingFrames);
                        sendResult = ffmpeg.avcodec_send_frame(codecContext, jobFrame);
                    }
                    Interlocked.Add(ref _encodeInputMicrosAccum, (long)(sendTimer.Elapsed.TotalMilliseconds * 1000));
                    Interlocked.Increment(ref _encodeInputCountAccum);
                    submissionLatency.Record(sendTimer.Elapsed);
                    if (sendResult == 0)
                    {
                        pendingFrames.Enqueue(job.FramePtr, job.WallClockUtc);
                        accepted = true;
                        Volatile.Write(ref _pendingEncoderFrames, pendingFrames.Count);
                        UpdatePeak(ref _peakPendingEncoderFrames, pendingFrames.PeakCount);
                        DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream, pendingFrames, outputLatency);
                    }
                    else
                    {
                        Interlocked.Increment(ref _sendFailedOtherCount);
                    }
                }
                finally
                {
                    if (!accepted) ffmpeg.av_frame_free(&jobFrame);
                }
            }

            // Queue drained and CompleteAdding was called (CaptureLoop's while
            // loop exited) - flush whatever's still buffered inside the encoder
            // itself, same as the original inline flush used to.
            ffmpeg.avcodec_send_frame(codecContext, null);
            DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream, pendingFrames, outputLatency);
        }
        catch (Exception error)
        {
            // Must not throw unhandled off this thread - an unobserved exception
            // on a plain Thread (unlike Task) crashes the whole process.
            AppLog.Error("Native capture: encode thread failed.", error);
        }
        finally
        {
            pendingFrames.ReleaseAll();
            Volatile.Write(ref _pendingEncoderFrames, 0);
        }
    }

    private unsafe void DrainToRingBuffer(AVCodecContext* codecContext, AVPacket* packet, AVFormatContext* fullSessionFormatContext, AVStream* fullSessionStream, EncoderFrameLifetimeQueue pendingFrames, ReplayLatencyHistogram? outputLatency = null)
    {
        while (true)
        {
            var receiveTimer = System.Diagnostics.Stopwatch.StartNew();
            var receiveResult = ffmpeg.avcodec_receive_packet(codecContext, packet);
            Interlocked.Add(ref _encodeOutputMicrosAccum, (long)(receiveTimer.Elapsed.TotalMilliseconds * 1000));
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF) break;
            if (receiveResult < 0) break;
            Interlocked.Increment(ref _encodeOutputCountAccum);

            var hasPendingFrame = pendingFrames.TryTake(out var pendingFrame);
            if (hasPendingFrame) outputLatency?.Record(MonotonicClock.UtcNow - pendingFrame.WallClockUtc);
            try
            {
            var isKeyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            // Pooled, not freshly allocated. This runs once per encoded packet -
            // 60 times a second for as long as a buffer is armed - and the
            // arrays live long enough to be promoted before the ring trims them
            // away, which is the worst possible shape for the GC: a real session
            // walked the managed heap from 23MB to 281MB across 20 gen2
            // collections, and one of those collections shows up in the log as a
            // 714ms capture stall.
            var copyTimer = System.Diagnostics.Stopwatch.StartNew();
            var data = _packetPayloads.Rent(packet->size);
            Marshal.Copy((IntPtr)packet->data, data, 0, packet->size);
            Interlocked.Add(ref _packetCopyMicrosAccum, (long)(copyTimer.Elapsed.TotalMilliseconds * 1000));
            Interlocked.Increment(ref _packetCopyCountAccum);

            // Per generation, not per session, and captured from the context
            // that actually produced this packet. The first packet out of each
            // encoder is what defines the avcC every clip cut from that
            // generation will be muxed under.
            lock (_bufferLock)
            {
                while (_encoderGenerations.Count <= _encoderGeneration) _encoderGenerations.Add(default);
                if (_encoderGenerations[_encoderGeneration].ExtraData is null && codecContext->extradata_size > 0)
                {
                    var extraData = new byte[codecContext->extradata_size];
                    Marshal.Copy((IntPtr)codecContext->extradata, extraData, 0, codecContext->extradata_size);
                    _encoderGenerations[_encoderGeneration] = new EncoderGenerationInfo(extraData, codecContext->codec_id, _timeBase);
                }
            }

            // Dequeues the real timestamp of whichever frame THIS packet
            // actually corresponds to (FIFO order, matching the encoder's own
            // in-order output guarantee with max_b_frames=0) - not just "now",
            // since the encoder can hold frames internally for a call or two
            // before releasing output, and packet->pts is now an IDEAL,
            // constant-rate timestamp (see the pacing gate in CaptureLoop) so
            // it can't be used to derive this the way it used to be.
            var realWallClockUtc = hasPendingFrame ? pendingFrame.WallClockUtc : MonotonicClock.UtcNow;

            Interlocked.Increment(ref _packetsOutCount);
            var insertTimer = System.Diagnostics.Stopwatch.StartNew();
            lock (_bufferLock)
            {
                _packets.Add(new RingPacket(data, packet->size, packet->pts, isKeyframe, realWallClockUtc, _encoderGeneration));
                _ringBufferBytes += packet->size;
                _ringBufferCapacityBytes += data.Length;
            }
            Interlocked.Add(ref _ringInsertMicrosAccum, (long)(insertTimer.Elapsed.TotalMilliseconds * 1000));
            Interlocked.Increment(ref _ringInsertCountAccum);

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

            }
            finally
            {
                if (hasPendingFrame) pendingFrames.Release(pendingFrame);
                Volatile.Write(ref _pendingEncoderFrames, pendingFrames.Count);
                ffmpeg.av_packet_unref(packet);
            }
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
            if (stream is null)
            {
                ffmpeg.avformat_free_context(formatContext);
                return false;
            }

            if (ffmpeg.avcodec_parameters_from_context(stream->codecpar, codecContext) < 0)
            {
                ffmpeg.avformat_free_context(formatContext);
                return false;
            }
            stream->time_base = codecContext->time_base;
            // On the zero-copy path the encoder's pix_fmt is AV_PIX_FMT_D3D11 -
            // an opaque hardware handle format that describes how frames get IN,
            // not what the stream contains. Copying it verbatim into the muxed
            // stream would advertise something no reader can make sense of; the
            // pixels are, and always were, NV12.
            if (codecContext->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                stream->codecpar->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            }

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
            var chunkStartUtc = sessionStartUtc;
            var remainingSeconds = durationSeconds;
            while (remainingSeconds > 0)
            {
                var chunkSeconds = Math.Min(SegmentChunkSeconds, remainingSeconds);
                // See SaveReplayAsync - a runt tail segment costs an ffmpeg
                // process per track for a fraction of a second of audio.
                if (remainingSeconds - chunkSeconds < SegmentChunkSeconds / 2)
                {
                    chunkSeconds = remainingSeconds;
                }

                segmentWindows.Add((chunkStartUtc, chunkSeconds));
                chunkStartUtc += TimeSpan.FromSeconds(chunkSeconds);
                remainingSeconds -= chunkSeconds;
            }

            var tracks = _audio
                .BuildAlignedTracksAsync(segmentWindows, config, snapshots, CancellationToken.None, capturesOverride, AudioSnapshotPurpose.BackgroundArchive)
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
                for (var i = 0; i < tracks.Count; i++)
                {
                    muxArgs.AddRange(new[] { $"-metadata:s:a:{i}", $"handler_name={tracks[i].Label}" });
                    muxArgs.AddRange(new[] { $"-metadata:s:a:{i}", $"title={tracks[i].Label}" });
                }
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

            // Copy only when requested session codec matches rolling encoder.
            // Auto replay can produce AV1, so a H.264 full-session request must
            // re-encode instead of silently writing an AV1 file under H.264
            // setting. AV1 re-encode still uses hardware only; failed hardware
            // conversion falls back to source stream copy.
            //
            // Vendor comes from ExportEncoderProbe, the same cached NVENC ->
            // AMF -> QSV detection the export and share paths use, rather than
            // assuming NVENC. Hardcoding it meant an AMD or Intel machine ran a
            // guaranteed-to-fail ffmpeg pass and then silently kept the larger
            // stream-copy file - the smaller-file feature simply never worked
            // off NVIDIA, and nothing said so.
            var sourceCodec = _videoCodecId == AVCodecID.AV_CODEC_ID_AV1 ? "AV1" : "H.264";
            var targetCodec = config.FullSessionVideoCodec switch
            {
                "AV1" => "AV1",
                "H.265" => "H.265",
                _ => "H.264"
            };
            var targetFamily = targetCodec == "AV1" ? ExportEncoderProbe.Av1Family : ExportEncoderProbe.Family;
            var codecArgs = (sourceCodec, targetCodec, targetFamily) switch
            {
                (var source, var target, _) when source == target => new[] { "-c:v", "copy" },
                (_, "H.264", "nvenc") => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "20", "-b:v", "0" },
                (_, "H.264", "amf") => new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20" },
                (_, "H.264", "qsv") => new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "20" },
                (_, "H.264", null) => new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20" },
                (_, "H.265", "nvenc") => new[] { "-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "24", "-b:v", "0" },
                (_, "H.265", "amf") => new[] { "-c:v", "hevc_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "24", "-qp_p", "24" },
                (_, "H.265", "qsv") => new[] { "-c:v", "hevc_qsv", "-preset", "medium", "-global_quality", "24" },
                (_, "AV1", "nvenc") => new[] { "-c:v", "av1_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "32", "-b:v", "0" },
                (_, "AV1", "amf") => new[] { "-c:v", "av1_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "32", "-qp_p", "32" },
                (_, "AV1", "qsv") => new[] { "-c:v", "av1_qsv", "-preset", "medium", "-global_quality", "32" },
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

            // Junctions are not followed: the default EnumerationOptions skips only
            // Hidden|System, so a `mklink /J` reparse point planted anywhere under the
            // user-configurable library folder would otherwise let this quota sweep
            // delete files outside the library entirely.
            var vodsEnumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                IgnoreInaccessible = true,
            };
            var vodsFullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vodsRoot)) + Path.DirectorySeparatorChar;

            var sessions = Directory.EnumerateFiles(vodsRoot, "*.*", vodsEnumeration)
                .Where(path => Path.GetFullPath(path).StartsWith(vodsFullRoot, StringComparison.OrdinalIgnoreCase))
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
    // Recycling waits while a save is holding references into the ring - see
    // BorrowWindowUnderLock. Trimming the entry out of _packets still happens
    // immediately; only handing the array back to the pool is deferred, so the
    // ring keeps its own accounting and a save can never read a payload that
    // has been re-rented under it.
    private void ReturnPooledPackets(int index, int count)
    {
        for (var i = index; i < index + count; i++)
        {
            var packet = _packets[i];
            _ringBufferBytes -= packet.Length;
            _ringBufferCapacityBytes -= packet.Data.Length;
            if (_borrowedWindowDepth > 0) _deferredPayloadReturns.Add(packet.Data);
            else _packetPayloads.Return(packet.Data);
        }
    }

    // Paired with BorrowWindowUnderLock. Once the last save in flight is done
    // with its references, everything trimmed while it ran goes back at once.
    private void ReleaseBorrowedWindow()
    {
        lock (_bufferLock)
        {
            if (_borrowedWindowDepth > 0) _borrowedWindowDepth--;
            if (_borrowedWindowDepth > 0 || _deferredPayloadReturns.Count == 0) return;

            foreach (var payload in _deferredPayloadReturns) _packetPayloads.Return(payload);
            _deferredPayloadReturns.Clear();
        }
    }

    // Selects the save window under the buffer lock and BORROWS its payloads -
    // no bytes are copied here.
    //
    // It used to copy every packet's bytes while holding the lock, which for a
    // 60s 1440p window is ~3000 allocations and a couple of hundred megabytes
    // memcpy'd with the ring locked. The encode thread takes that same lock for
    // every packet it produces, so each save stalled encoding outright: three
    // saves inside 32 seconds drove the encode queue to 29/30 and 17 dropped
    // frames while the encoder itself was running at 0.63ms a frame, and the
    // tuner then halved the capture rate blaming an encoder that was never
    // behind. Clips saved during that stretch came out at 27-48fps.
    //
    // Borrowing instead means the lock is held only for two index scans. What
    // the copy was protecting against - TrimRingBuffer recycling a payload into
    // a new packet while the remux is still reading it - is handled by deferring
    // pool returns for as long as any save holds a window (see
    // ReturnPooledPackets/ReleaseBorrowedWindow). Entries still leave the ring
    // on schedule; only the arrays wait.
    //
    // Every caller MUST pair this with ReleaseBorrowedWindow in a finally, or
    // the pool stops recycling for the rest of the session.
    private RingPacket[] BorrowWindowUnderLock(DateTime requestedStartUtc, DateTime requestedEndUtc, bool startAtOrAfterRequestedUtc = false)
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
    {
        lock (_bufferLock)
        {
            if (_packets.Count == 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

            // Saving from a keyframe immediately before the requested event
            // keeps the remux playable while still producing an event-sized clip.
            var startIndex = -1;
            if (startAtOrAfterRequestedUtc)
            {
                for (var i = 0; i < _packets.Count; i++)
                {
                    if (_packets[i].WallClockUtc >= requestedStartUtc && _packets[i].IsKeyframe) { startIndex = i; break; }
                }
            }
            else
            {
                for (var i = _packets.Count - 1; i >= 0; i--)
                {
                    if (_packets[i].WallClockUtc <= requestedStartUtc && _packets[i].IsKeyframe) { startIndex = i; break; }
                }
                if (startIndex < 0) startIndex = _packets.FindIndex(packet => packet.IsKeyframe);
            }
            if (startIndex < 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

            var endIndex = -1;
            for (var i = _packets.Count - 1; i >= 0; i--)
            {
                if (_packets[i].WallClockUtc <= requestedEndUtc) { endIndex = i; break; }
            }
            if (endIndex < startIndex) throw new InvalidOperationException("The requested replay window is no longer available.");

            // A live encoder failover swaps SPS/PPS mid-ring while the packets
            // themselves carry none (AV_CODEC_FLAG_GLOBAL_HEADER). One clip is
            // one avcC, so a window that crosses a generation boundary can only
            // be muxed correctly for one side of it - the whole other side would
            // decode as garbage. Keep the newest generation and drop what came
            // before it: a short clip that plays beats a full-length one that
            // does not.
            var latestGeneration = _packets[endIndex].Generation;
            if (_packets[startIndex].Generation != latestGeneration)
            {
                var boundary = -1;
                for (var i = endIndex; i >= startIndex; i--)
                {
                    if (_packets[i].Generation != latestGeneration) break;
                    if (_packets[i].IsKeyframe) boundary = i;
                }
                if (boundary < 0) throw new InvalidOperationException("The encoder was just replaced. Try again in a second.");
                var droppedSeconds = (_packets[boundary].WallClockUtc - _packets[startIndex].WallClockUtc).TotalSeconds;
                AppLog.Info($"Native replay save: encoder changed mid-buffer - clip starts at the new encoder's first keyframe, {droppedSeconds:F1}s of older history left out.");
                _lastSaveTrimmedBySeconds = droppedSeconds;
                startIndex = boundary;
            }
            else
            {
                _lastSaveTrimmedBySeconds = 0;
            }

            var window = new RingPacket[endIndex - startIndex + 1];
            _packets.CopyTo(startIndex, window, 0, window.Length);
            // Payloads are now referenced by this window as well as the ring.
            // Readers use RingPacket.Length, never Data.Length, so a pooled
            // array being longer than its packet does not matter.
            _borrowedWindowDepth++;
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

    private unsafe void RemuxWindowToMp4(RingPacket[] window, string outputPath, DateTime requestedEndUtc, bool variableFrameTiming)
    {
        AVFormatContext* formatContext = null;
        try
        {
            ffmpeg.avformat_alloc_output_context2(&formatContext, null, "mp4", outputPath);
            if (formatContext is null) throw new InvalidOperationException("avformat_alloc_output_context2 failed.");

            var stream = ffmpeg.avformat_new_stream(formatContext, null);
            if (stream is null) throw new InvalidOperationException("avformat_new_stream failed.");

            // The window is single-generation by construction (see
            // BorrowWindowUnderLock), so the parameter sets that describe these
            // exact slices are the ones recorded for that generation. Muxing any
            // other generation's avcC here is what made whole clips undecodable.
            var generation = window[0].Generation;
            EncoderGenerationInfo info;
            lock (_bufferLock)
            {
                info = generation >= 0 && generation < _encoderGenerations.Count
                    ? _encoderGenerations[generation]
                    : default;
            }
            if (info.ExtraData is null or { Length: 0 })
                throw new InvalidOperationException($"No encoder parameter sets were recorded for generation {generation}.");

            stream->time_base = info.TimeBase.den == 0 ? _timeBase : info.TimeBase;
            stream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            stream->codecpar->codec_id = info.CodecId;
            stream->codecpar->codec_tag = 0;
            stream->codecpar->width = _outputWidth;
            stream->codecpar->height = _outputHeight;

            var extraDataPtr = (byte*)ffmpeg.av_mallocz((ulong)info.ExtraData.Length);
            if (extraDataPtr is null) throw new InvalidOperationException("av_mallocz failed for codec extradata.");
            Marshal.Copy(info.ExtraData, 0, (IntPtr)extraDataPtr, info.ExtraData.Length);
            stream->codecpar->extradata = extraDataPtr;
            stream->codecpar->extradata_size = info.ExtraData.Length;

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
            // Only ever read for the final packet, which has no successor to
            // measure against; every other one overwrites it first.
            long lastPacketDuration = 0;
            var packet = ffmpeg.av_packet_alloc();
            try
            {
                for (var i = 0; i < window.Length; i++)
                {
                    var ringPacket = window[i];
                    // Length, not Data.Length - see RingPacket. These particular
                    // packets came from CopyWindowUnderLock and so are already
                    // exact-sized, but reading the field keeps this correct if
                    // a pooled packet ever reaches here.
                    var newPacketResult = ffmpeg.av_new_packet(packet, ringPacket.Length);
                    if (newPacketResult < 0 || packet->data is null)
                        throw new InvalidOperationException($"av_new_packet failed ({newPacketResult}).");
                    Marshal.Copy(ringPacket.Data, 0, (IntPtr)packet->data, ringPacket.Length);
                    packet->pts = packet->dts = ringPacket.PtsMs - basePts;
                    // Explicit, because variable-frame-rate packets can be far
                    // apart. The final VFR packet holds its image until the
                    // requested save moment; CFR retains its preceding cadence.
                    packet->duration = i + 1 < window.Length
                        ? window[i + 1].PtsMs - ringPacket.PtsMs
                        : variableFrameTiming
                            ? Math.Max(1, (long)Math.Round((requestedEndUtc - ringPacket.WallClockUtc).TotalMilliseconds * 1_000))
                            : Math.Max(1, lastPacketDuration);
                    lastPacketDuration = packet->duration;
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
    private static IEnumerable<string> EncoderCandidates(ReplayBufferConfig config)
        => ReplayVideoCodecPolicy.Candidates(
            config.VideoCodec,
            ReplayVideoCodecPolicy.Normalize(config.VideoCodec) == ReplayVideoCodecPolicy.Av1 ? ExportEncoderProbe.Av1Family : null,
            config.EncoderMode);

    // Encoder-specific options are applied to priv_data before avcodec_open2.
    // A failed optional setting is logged and skipped so an older driver/build
    // remains usable. Bitrate also bounds the in-memory packet ring, so its
    // persisted 5-100 Mbps range is clamped here rather than trusted.
    private static long FixedBitrate(ReplayBufferConfig config) => Math.Clamp(config.BitrateMbps, 5, 100) * 1_000_000L;

    private static unsafe void ApplyReplayThroughputEncoderOptions(AVCodecContext* codecContext, string candidateName, ReplayBufferConfig config, bool lowPower = false)
    {
        void TrySet(string name, string value)
        {
            var result = ffmpeg.av_opt_set(codecContext->priv_data, name, value, 0);
            if (result < 0)
            {
                AppLog.Info($"Native encoder probe: {candidateName} option {name}={value} not supported (error {result}), skipping.");
            }
        }

        void ApplyAutomaticNvencOptions(string? profile)
        {
            // Medal exposes automatic GPU selection, not raw NVENC P1-P7
            // controls. Use one replay profile that favors sustainable capture
            // throughput, then keep enough surfaces in flight that send_frame
            // does not serialize behind the previous hardware submission.
            TrySet("preset", "p1");
            if (profile is not null) TrySet("profile", profile);
            TrySet("tune", "ll");
            TrySet("surfaces", ReplayEncoderProfilePolicy.NvencSurfaces(config.FrameRate).ToString(CultureInfo.InvariantCulture));
            var delay = Environment.GetEnvironmentVariable("CLYPDAT_NVENC_DELAY")?.Trim();
            if (delay is "4" or "8") TrySet("delay", delay);
            // This is a replay buffer, not a live stream. NVENC's zerolatency
            // option forces a packet out for every submitted frame; when the
            // game is contending for the GPU, that turns avcodec_send_frame
            // into a synchronous wait and collapses a 60fps capture to about
            // 52fps. Leave the normal small hardware pipeline enabled instead:
            // its few queued frames are invisible inside a 60-second buffer,
            // while the capture thread can keep providing a full, even cadence.
            // AQ and lookahead optimize compression efficiency at the cost of
            // additional per-frame GPU work. The recorder prefers fresh frames
            // while a game is saturating the GPU; bitrate remains user-selected.
            TrySet("spatial-aq", "0");
            TrySet("temporal-aq", "0");
            TrySet("rc-lookahead", "0");
            TrySet("rc", "cbr");
            TrySet("forced-idr", "1");
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
                ApplyAutomaticNvencOptions("high");
                break;
            case "av1_nvenc":
                ApplyAutomaticNvencOptions(null);
                break;
            // The automatic profile chooses the fastest established settings for
            // every hardware family. EncoderCandidates still falls through
            // NVIDIA -> AMD -> Intel -> CPU when a device is unavailable.
            case "h264_amf":
                TrySet("usage", "ultralowlatency");
                TrySet("quality", "speed");
                TrySet("rc", "cbr");
                // AMF's equivalent knob is spelled with an underscore, not a
                // dash like NVENC's - this was silently a no-op under the
                // wrong name (TrySet just logs and moves on for an unknown
                // option), so AMD captures weren't actually getting true IDR
                // cut points despite the setting looking present here.
                TrySet("forced_idr", "1");
                break;
            case "av1_amf":
                TrySet("usage", "ultralowlatency");
                TrySet("quality", "speed");
                TrySet("rc", "hqcbr");
                TrySet("forced_idr", "1");
                break;
            case "h264_qsv":
            case "av1_qsv":
                TrySet("preset", "veryfast");
                TrySet("rc_mode", "cbr");
                TrySet("forced_idr", "1");
                // Back to QSV's own default of 4. This was pinned to 1 out of
                // the same latency concern as NVENC's "ll" tune above - and
                // that reasoning was already reversed there, on the grounds
                // that this is a ring buffer being written to memory, not a
                // live stream with a latency budget. The identical argument
                // applies: depth 4 means up to ~66ms of frames sit inside the
                // encoder before their packets reach the ring, which against a
                // 60-second buffer is noise. What depth 1 actually bought was a
                // fully serialised encoder - avcodec_send_frame blocking on the
                // previous frame's completion, measured at avgEncodeMs 8.6-10.2
                // at 1080p60 on an Iris Xe laptop.
                TrySet("async_depth", "4");
                // Routes encoding to the fixed-function VDENC block instead of
                // the EU-based path. On an integrated Intel GPU the EU path
                // competes with the game for the very same execution units,
                // which is the difference between "capture costs some GPU" and
                // "the laptop chugs and is hard to control". Not supported in
                // every preset/rate-control/driver combination, and it fails at
                // avcodec_open2 rather than here - see CreateEncoder, which
                // retries QSV without it.
                if (lowPower) TrySet("low_power", "1");
                break;
            case "libx264":
                TrySet("preset", "ultrafast");
                TrySet("tune", "zerolatency");
                TrySet("bitrate", FixedBitrate(config).ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    // D3D11_BIND_RENDER_TARGET. NVENC takes a D3D11 texture as an input
    // resource directly, but only one it can bind - ffmpeg's own default for a
    // d3d11va frames pool is D3D11_BIND_DECODER, which is for the decode path
    // and is not what an encoder input wants.
    private const uint D3D11BindRenderTarget = 0x20;

    // Pool slots beyond the encode queue's own capacity: one for the frame
    // being filled, one held back for padding ticks, and two of slack so a
    // frame the encoder has finished with but not yet unreferenced can't
    // starve the next capture tick.
    private const int HardwareFramePoolHeadroom = 4;

    private static unsafe void ReleaseHardwareFrames(ref nint deviceRef, ref nint framesRef)
    {
        if (framesRef != 0)
        {
            var frames = (AVBufferRef*)framesRef;
            ffmpeg.av_buffer_unref(&frames);
            framesRef = 0;
        }

        if (deviceRef != 0)
        {
            var device = (AVBufferRef*)deviceRef;
            ffmpeg.av_buffer_unref(&device);
            deviceRef = 0;
        }
    }

    // The encoder's own NV12 frame pool, living on the SAME ID3D11Device the
    // capture and the Video Processor already use, so a scaled frame reaches
    // NVENC without ever leaving the GPU.
    //
    // What this replaces: the scaled NV12 surface was copied to a staging
    // texture, mapped, memcpy'd plane by plane into a system-memory AVFrame,
    // and then handed to ffmpeg - which uploaded those same pixels straight
    // back to the GPU for NVENC. 5.5MB down and 5.5MB back up per frame at
    // 1440p60, on the same GPU a game already owns. Measured cost of that
    // round trip under real load: avgEncodeMs of 65-110ms per frame against a
    // 16.7ms budget (0.9-2.6ms with the GPU idle), which pinned the encode
    // queue at capacity and made the pacing loop skip pad ticks - a clip
    // configured for 60fps came out at 53.5.
    //
    // Best-effort by design: every failure path here returns (0, 0) and the
    // caller stays on the system-memory path, which still works exactly as it
    // did.
    private static readonly uint[] EncodeFramePoolBindFlags = { 0, D3D11BindRenderTarget, 0x8 };

    private static unsafe (nint DeviceRef, nint FramesRef) TryCreateD3D11EncodeFrames(
        ID3D11Device device, int width, int height, int poolSize)
    {
        // RTX 4070 Ti accepts FFmpeg's dynamic NV12 allocation. Request it
        // first; fixed texture arrays remain a startup-only fallback.
        foreach (var size in new[] { 0, poolSize })
        {
            foreach (var bindFlags in EncodeFramePoolBindFlags)
            {
                var attempt = TryCreateD3D11EncodeFrames(device, width, height, size, bindFlags);
                if (attempt.FramesRef != 0 && CanAllocateD3D11EncodeFrame(attempt.FramesRef))
                {
                    AppLog.Info($"Native capture: D3D11 encode frame pool ready (poolSize={size}, bindFlags=0x{bindFlags:X}).");
                    return attempt;
                }
                ReleaseHardwareFrames(ref attempt.DeviceRef, ref attempt.FramesRef);
            }
        }

        return (0, 0);
    }

    private static unsafe bool CanAllocateD3D11EncodeFrame(nint framesRef)
    {
        AVFrame* frame = null;
        try
        {
            frame = ffmpeg.av_frame_alloc();
            return frame is not null &&
                   ffmpeg.av_hwframe_get_buffer((AVBufferRef*)framesRef, frame, 0) >= 0;
        }
        finally
        {
            if (frame is not null) ffmpeg.av_frame_free(&frame);
        }
    }

    private static unsafe (nint DeviceRef, nint FramesRef) TryCreateD3D11EncodeFrames(
        ID3D11Device device, int width, int height, int poolSize, uint bindFlags)
    {
        AVBufferRef* deviceRef = null;
        AVBufferRef* framesRef = null;
        try
        {
            deviceRef = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (deviceRef is null) return (0, 0);

            var deviceContext = (AVHWDeviceContext*)deviceRef->data;
            var d3dDevice = (AVD3D11VADeviceContext*)deviceContext->hwctx;
            // ffmpeg takes ownership of one reference and releases it when the
            // context is freed, so this AddRef is the reference it consumes -
            // without it, freeing the hw device would tear down a device the
            // capture loop is still using.
            device.AddRef();
            d3dDevice->device = (FFmpeg.AutoGen.ID3D11Device*)device.NativePointer;
            // device_context/video_device are deliberately left null: ffmpeg's
            // own init fills them in from the device (and marks it
            // multithread-protected, which the encode thread touching it from
            // off the capture thread requires).
            var deviceInit = ffmpeg.av_hwdevice_ctx_init(deviceRef);
            if (deviceInit < 0)
            {
                AppLog.Info($"Native capture: D3D11 encode device init failed (error {deviceInit}), using system-memory frames.");
                ffmpeg.av_buffer_unref(&deviceRef);
                return (0, 0);
            }

            framesRef = ffmpeg.av_hwframe_ctx_alloc(deviceRef);
            if (framesRef is null)
            {
                ffmpeg.av_buffer_unref(&deviceRef);
                return (0, 0);
            }

            var framesContext = (AVHWFramesContext*)framesRef->data;
            framesContext->format = AVPixelFormat.AV_PIX_FMT_D3D11;
            framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            framesContext->width = width;
            framesContext->height = height;
            // Fixed pool when driver accepts texture arrays; size 0 requests
            // ffmpeg's dynamic single-texture pool. Both stay bounded by the
            // encode queue and headroom.
            framesContext->initial_pool_size = poolSize;
            var d3dFrames = (AVD3D11VAFramesContext*)framesContext->hwctx;
            d3dFrames->BindFlags = bindFlags;

            var framesInit = ffmpeg.av_hwframe_ctx_init(framesRef);
            if (framesInit < 0)
            {
                AppLog.Info($"Native capture: D3D11 encode frame pool init failed (error {framesInit}), using system-memory frames.");
                ffmpeg.av_buffer_unref(&framesRef);
                ffmpeg.av_buffer_unref(&deviceRef);
                return (0, 0);
            }

            return ((nint)deviceRef, (nint)framesRef);
        }
        catch (Exception error)
        {
            AppLog.Error("Native capture: D3D11 encode frame setup failed, using system-memory frames.", error);
            if (framesRef is not null) ffmpeg.av_buffer_unref(&framesRef);
            if (deviceRef is not null) ffmpeg.av_buffer_unref(&deviceRef);
            return (0, 0);
        }
    }

    // A successful one-frame send proves only that NVENC can register a D3D11
    // resource. It does not prove that the path can maintain the configured
    // capture rate while it copies, submits, and drains packets. Qualify both
    // inputs on fresh throwaway contexts; no probe timestamps or delayed frames
    // can therefore reach the replay timeline.
    private static unsafe NvencInputPathQualification.Result BenchmarkNvencInput(
        AVCodecContext* codecContext, AVBufferRef* framesRef, ID3D11Device? device,
        int width, int height, bool hardwareFrames, AVFrame* foregroundFrame = null,
        ID3D11Texture2D? foregroundTexture = null)
    {
        var packet = ffmpeg.av_packet_alloc();
        AVFrame* softwareTemplate = null;
        var poolTextures = new Dictionary<nint, ID3D11Texture2D>();
        var ownsSourceTexture = false;
        ID3D11Texture2D? sourceTexture = foregroundTexture;
        try
        {
            if (packet is null) return new(false, 0);
            if (hardwareFrames)
            {
                if (framesRef is null || device is null) return new(false, 0);
                if (sourceTexture is null)
                {
                    // This path is only used by recovery before the next real
                    // frame arrives. It is never used for initial qualification.
                    sourceTexture = device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                        Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default, BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.None
                    });
                    ownsSourceTexture = true;
                }
            }
            else
            {
                if (foregroundFrame is null) return new(false, 0);
                softwareTemplate = ffmpeg.av_frame_clone(foregroundFrame);
                if (softwareTemplate is null) return new(false, 0);
            }

            var windows = new List<double>(ReplayEncoderQualificationPolicy.RequiredWindows);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var warm = false;
            var emitted = 0;
            var windowPackets = 0;
            var windowStarted = TimeSpan.Zero;
            long pts = 0;
            while (windows.Count < ReplayEncoderQualificationPolicy.RequiredWindows)
            {
                if (!warm && stopwatch.Elapsed >= TimeSpan.FromSeconds(2))
                    return new(false, 0, TimedOut: true);

                AVFrame* input = null;
                if (hardwareFrames)
                {
                    input = ffmpeg.av_frame_alloc();
                    if (input is null || ffmpeg.av_hwframe_get_buffer(framesRef, input, 0) < 0)
                    {
                        if (input is not null) ffmpeg.av_frame_free(&input);
                        return new(false, 0);
                    }
                    var texturePointer = (nint)input->data[0];
                    if (!poolTextures.TryGetValue(texturePointer, out var poolTexture))
                    {
                        poolTexture = new ID3D11Texture2D(texturePointer);
                        poolTexture.AddRef();
                        poolTextures.Add(texturePointer, poolTexture);
                    }
                    device!.ImmediateContext.CopySubresourceRegion(poolTexture, (uint)(nint)input->data[1], 0, 0, 0, sourceTexture!, 0);
                }
                else
                {
                    input = ffmpeg.av_frame_clone(softwareTemplate);
                    if (input is null) return new(false, 0);
                }

                input->pts = pts++;
                var send = ffmpeg.avcodec_send_frame(codecContext, input);
                ffmpeg.av_frame_free(&input);
                if (send < 0 && send != ffmpeg.AVERROR(ffmpeg.EAGAIN)) return new(false, 0);

                while (ffmpeg.avcodec_receive_packet(codecContext, packet) >= 0)
                {
                    emitted++;
                    if (warm) windowPackets++;
                    ffmpeg.av_packet_unref(packet);
                }

                if (!warm && emitted > 0)
                {
                    warm = true;
                    windowStarted = stopwatch.Elapsed;
                    windowPackets = 0;
                }
                else if (warm && stopwatch.Elapsed - windowStarted >= TimeSpan.FromSeconds(1))
                {
                    var elapsed = Math.Max((stopwatch.Elapsed - windowStarted).TotalSeconds, 0.001);
                    windows.Add(windowPackets / elapsed);
                    windowStarted = stopwatch.Elapsed;
                    windowPackets = 0;
                }
            }

            return new(true, windows.Average(), WindowFramesPerSecond: windows);
        }
        catch (Exception error)
        {
            AppLog.Info($"Native encoder qualification: {(hardwareFrames ? "D3D11" : "system-memory")} path failed ({error.Message}).");
            return new(false, 0);
        }
        finally
        {
            foreach (var texture in poolTextures.Values) texture.Dispose();
            if (ownsSourceTexture) sourceTexture?.Dispose();
            if (softwareTemplate is not null) ffmpeg.av_frame_free(&softwareTemplate);
            if (packet is not null) ffmpeg.av_packet_free(&packet);
        }
    }

    private static unsafe AVCodecContext* CreateEncoder(ReplayBufferConfig config, int width, int height, out AVRational timeBase, out string encoderName)
        => CreateEncoder(config, width, height, 0, null, out timeBase, out encoderName, out _, null, null);

    private static unsafe AVCodecContext* CreateEncoder(
        ReplayBufferConfig config,
        int width,
        int height,
        nint hardwareFramesRef,
        ID3D11Device? hardwareDevice,
        out AVRational timeBase,
        out string encoderName,
        out bool usingHardwareFrames,
        AVFrame* foregroundFrame = null,
        ID3D11Texture2D? foregroundTexture = null,
        IReadOnlyList<ReplayEncoderCandidate>? candidateOrder = null)
    {
        usingHardwareFrames = false;
        // Local copy because a local function cannot capture an out parameter.
        var encoderTimeBase = new AVRational { num = 1, den = 1_000_000 };
        timeBase = encoderTimeBase;
        encoderName = string.Empty;
        // One full alloc/configure/open attempt. Returns null if the open
        // fails - which is the only way some options can report unsupported
        // (low_power in particular is accepted by av_opt_set and rejected by
        // avcodec_open2), and a context whose open failed is unusable, so
        // every attempt has to start from a fresh allocation rather than
        // re-opening this one.
        AVCodecContext* TryOpen(AVCodec* candidateCodec, string candidateName, bool lowPower, bool hardwareFrames)
        {
            var codecContext = ffmpeg.avcodec_alloc_context3(candidateCodec);
            codecContext->width = width;
            codecContext->height = height;
            codecContext->time_base = encoderTimeBase;
            codecContext->framerate = new AVRational { num = Math.Clamp(config.FrameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate), den = 1 };
            codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            // Limited/studio range (16-235), matching what the scalers
            // (CreateScaler/CreateGpuScaler) now write, and tagged as such.
            //
            // This was AVCOL_RANGE_JPEG with full-range pixels to match, which
            // is internally consistent and correct by the spec - and still came
            // out visibly dark everywhere outside this app. Consumers built on
            // Media Foundation (Explorer's thumbnails and preview, Photos,
            // Films & TV) ignore H.264's video_full_range_flag, assume limited,
            // and expand 16-235 -> 0-255 on content that already occupies the
            // full range: blacks clip to 0 and the whole picture crushes down.
            // LibVLC, and so the editor's own playback, makes the same
            // assumption. Emitting limited range makes the flag-ignorers right
            // by construction, and the flag-honourers (ffmpeg, and so every
            // generated thumbnail and filmstrip) read the tag and agree - which
            // is the same reason every other capture tool defaults to limited.
            codecContext->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            // Both scalers convert with BT.709 coefficients, but these tags
            // were never written, leaving files marked "unknown" - which every
            // player then resolves to BT.709 for HD anyway. That accidentally
            // worked only because the conversion side has now been moved to 709
            // to match; tagging it explicitly is what actually makes the two
            // ends agree instead of relying on a coincidence of defaults.
            codecContext->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            codecContext->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            codecContext->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            var maxBitrate = FixedBitrate(config);
            codecContext->bit_rate = maxBitrate;
            // A real VBV to spend against, rather than leaving it unset and
            // letting the encoder fall back to an effectively per-frame budget.
            // One second of buffer lets a burst of hard-to-compress motion
            // borrow bits from the quiet stretch around it, which is the whole
            // mechanism that keeps fast gameplay from going soft. rc_max_rate
            // is the hard ceiling from the user's quality setting - with
            // constant-quality rate control it's this, not bit_rate, that bounds
            // how large a clip (and the in-memory ring buffer) can actually get.
            codecContext->rc_buffer_size = (int)maxBitrate;
            codecContext->rc_max_rate = maxBitrate;
            codecContext->gop_size = ReplayEncoderProfilePolicy.GopFrames(config.FrameRate);
            codecContext->max_b_frames = 0;
            codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            // Hardware input: the frames the capture loop hands over ARE D3D11
            // textures, so that is the context's pixel format and the pool it
            // draws them from. sw_format on the pool (NV12) is what actually
            // reaches the encoder, so nothing else in the configuration above
            // changes between the two paths.
            if (hardwareFrames)
            {
                codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                codecContext->hw_frames_ctx = ffmpeg.av_buffer_ref((AVBufferRef*)hardwareFramesRef);
                var framesContext = (AVHWFramesContext*)((AVBufferRef*)hardwareFramesRef)->data;
                codecContext->hw_device_ctx = framesContext is null
                    ? null
                    : ffmpeg.av_buffer_ref(framesContext->device_ref);
                if (codecContext->hw_frames_ctx is null || codecContext->hw_device_ctx is null)
                {
                    AppLog.Info($"Native encoder probe: {candidateName} could not retain the D3D11 frame/device context.");
                    var unusableContext = codecContext;
                    ffmpeg.avcodec_free_context(&unusableContext);
                    return null;
                }
            }

            ApplyReplayThroughputEncoderOptions(codecContext, candidateName, config, lowPower);

            var openResult = ffmpeg.avcodec_open2(codecContext, candidateCodec, null);
            if (openResult == 0) return codecContext;

            AppLog.Info($"Native encoder probe: {candidateName} failed to open (error {openResult}, lowPower={lowPower}, hardwareFrames={hardwareFrames}).");
            var failedContext = codecContext;
            ffmpeg.avcodec_free_context(&failedContext);
            return null;
        }

        if (foregroundFrame is null)
        {
            // Keep capture/audio infrastructure armable while the game is not
            // foreground. This opens a context only to keep the existing
            // thread wiring alive; it sends no frames and is replaced below.
            foreach (var candidate in candidateOrder ?? ReplayEncoderQualificationPolicy.StartupCandidates(config.VideoCodec, config.FrameRate, config.EncoderMode))
            {
                var codec = ffmpeg.avcodec_find_encoder_by_name(candidate.Name);
                if (codec is null) continue;
                var hardware = candidate.IsD3D11 && hardwareFramesRef != 0;
                if (candidate.IsD3D11 && !hardware)
                {
                    AppLog.Info($"Native replay startup: {candidate.Name}/{candidate.InputPath} unavailable - D3D11 pool unavailable.");
                    continue;
                }
                var context = TryOpen(codec, candidate.Name, candidate.InputPath == ReplayEncoderInputPath.LowPower, hardware);
                if (context is null) continue;
                encoderName = candidate.Name;
                usingHardwareFrames = hardware;
                return context;
            }
            throw new InvalidOperationException("No replay encoder context could be armed while waiting for a foreground frame.");
        }

        // A replay buffer must not hold the first foreground frame hostage
        // while it benchmarks every available encoder.  Those trials used to
        // take 6-8 seconds on a healthy RTX 4070 Ti even after NVENC had
        // already proved usable.  Open the preferred viable production context
        // now; the normal rolling health monitor validates its real gameplay
        // output instead of synthetic trial frames.
        foreach (var candidate in candidateOrder ?? ReplayEncoderQualificationPolicy.StartupCandidates(config.VideoCodec, config.FrameRate, config.EncoderMode))
        {
            var candidateCodec = ffmpeg.avcodec_find_encoder_by_name(candidate.Name);
            if (candidateCodec is null)
            {
                AppLog.Info($"Native replay startup: {candidate.Name}/{candidate.InputPath} unavailable - encoder not present.");
                continue;
            }

            var hardware = candidate.IsD3D11 && hardwareFramesRef != 0;
            if (candidate.IsD3D11 && !hardware)
            {
                AppLog.Info($"Native replay startup: {candidate.Name}/{candidate.InputPath} unavailable - D3D11 pool unavailable.");
                continue;
            }

            var context = TryOpen(candidateCodec, candidate.Name, candidate.InputPath == ReplayEncoderInputPath.LowPower, hardware);
            if (context is null)
            {
                continue;
            }

            encoderName = candidate.Name;
            usingHardwareFrames = hardware;
            AppLog.Info($"Native replay startup: selected {encoderName}/{candidate.InputPath}; the first returned packet primes rolling production validation.");
            return context;
        }

        throw new InvalidOperationException("No replay encoder context could be opened for the first foreground frame.");
    }

    private static ReplayEncoderCandidate ResolveEncoderCandidate(ReplayBufferConfig config, string encoderName, bool hardwareFrames)
    {
        var candidates = ReplayEncoderQualificationPolicy.StartupCandidates(config.VideoCodec, config.FrameRate, config.EncoderMode);
        var match = candidates.FirstOrDefault(candidate =>
                   string.Equals(candidate.Name, encoderName, StringComparison.OrdinalIgnoreCase) &&
                   (candidate.IsD3D11 == hardwareFrames));
        return !string.IsNullOrEmpty(match.Name)
            ? match
            : candidates.First(candidate => string.Equals(candidate.Name, encoderName, StringComparison.OrdinalIgnoreCase));
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

    private static ID3D11Device CreateD3D11Device(out int? appliedGpuPriority)
    {
        var levels = new[]
        {
            Vortice.Direct3D.FeatureLevel.Level_11_1,
            Vortice.Direct3D.FeatureLevel.Level_11_0,
            Vortice.Direct3D.FeatureLevel.Level_10_1,
        };
        // The immediate context is captured and released, NOT discarded with
        // `out _`. This overload hands back its own AddRef'd wrapper for it, and
        // a live COM reference on the context keeps the entire D3D11 device
        // alive no matter how thoroughly the device itself is disposed. Measured
        // at 14.4MB of private bytes per created-and-disposed device - which the
        // stall-recovery loop below was doing every 2 seconds, indefinitely,
        // for a steady ~7MB/s native leak that no GC could reach (it is not
        // managed memory) and that took the process past 16GB.
        //
        // Safe to release: this is a distinct wrapper from the one the device
        // caches for its own ImmediateContext property (verified by reference
        // comparison), so the per-frame device.ImmediateContext calls elsewhere
        // are unaffected.
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device, out _, out Vortice.Direct3D11.ID3D11DeviceContext? createdContext).CheckError();
        createdContext?.Dispose();

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

        // Both halves of the priority story - see GpuScheduling. The process
        // class has to be raised from here rather than at app startup because
        // it only matters once there is GPU work to schedule, and this is the
        // one place that work begins; it self-guards against the repeat calls
        // that a buffer restart causes.
        GpuScheduling.TryRaiseProcessGpuPriority();
        appliedGpuPriority = GpuScheduling.TryRaiseDeviceGpuPriority(device!.NativePointer, "processing");
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

    // Data comes from PacketPayloadPool and is usually longer than the packet.
    // it holds - Length, not Data.Length, is the packet. Every consumer must
    // respect that.
    private readonly record struct RingPacket(byte[] Data, int Length, long PtsMs, bool IsKeyframe, DateTime WallClockUtc, int Generation);

    // The SPS/PPS and codec id of one encoder generation, plus the time base its
    // packet timestamps are expressed in. A clip is muxed entirely from one of
    // these; BorrowWindowUnderLock guarantees the window never spans two.
    private readonly record struct EncoderGenerationInfo(byte[]? ExtraData, AVCodecID CodecId, AVRational TimeBase);

    private readonly record struct PauseEvent(DateTime WallClockUtc, bool IsPaused);
}
