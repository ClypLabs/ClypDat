namespace ClypDat.Capture.Abstractions;

// An event-derived section of the replay timeline. Manual saves leave this null
// and continue to use the user's configured replay duration.
public sealed record ReplayClipWindow(DateTime StartUtc, DateTime EndUtc);

public enum ReplayCaptureState
{
    Unknown,
    Starting,
    Healthy,
    Degraded,
    Failed,
    Stopped,
    // Worker process or its IPC channel vanished. The proxy keeps replay
    // logically armed while it reconnects or starts a replacement worker.
    Recovering
}

public enum ReplayCaptureStartupPhase
{
    None,
    WaitingForForeground,
    OpeningEncoder,
    Ready,
    Fallback
}

// Why a backend reported Degraded. Both causes look identical through State
// alone, and they call for opposite responses: an encoder overload means the
// encode settings are too expensive for this machine, while a stall means the
// display stopped handing over frames (occlusion, driver reset, mode change)
// with the encoder sitting idle and blameless. Anything reacting to Degraded -
// notably the encoder auto-tuner - has to tell them apart, and matching on the
// human-readable LastFailure text is far too brittle to hang that on.
public enum ReplayStorageState
{
    Healthy,
    Warning,
    Critical,
    Inaccessible
}

public sealed record ReplayStorageHealth(
    ReplayStorageState State,
    long FreeBytes,
    double RecentWriteLatencyMs,
    double PeakWriteLatencyMs,
    string VolumeRole,
    string Reason,
    DateTime UpdatedUtc)
{
    public static ReplayStorageHealth Unknown => new(ReplayStorageState.Healthy, -1, 0, 0, string.Empty, string.Empty, DateTime.UtcNow);
}

public enum ReplayDegradeReason
{
    None,
    EncoderOverload,
    CaptureStall,
    CaptureTransport
}

// The first failing boundary in the recorder.  Keep this separate from the
// compatibility degrade reason: a slow source must never make the encoder
// tuner sacrifice quality.
public enum ReplayPipelineStage
{
    None,
    SourceAcquisition,
    CaptureTransport,
    FrameProcessing,
    Pacing,
    EncodeQueue,
    EncoderSubmission,
    EncoderCompletion
}

public enum ReplayRecoveryStopReason
{
    None,
    WorkerCrashLoop,
    CapturePipelineStall
}

public enum ReplayPipelineRecoveryAction
{
    None,
    RecreateDxgi,
    SwitchToWgc,
    RestartWorker
}

// Keep capture health separate from IReplayBuffer. Old/third-party backends can
// remain valid while callers opt into richer diagnostics where available.
public sealed record ReplayCaptureHealth(
    string Backend,
    string CaptureMode,
    ReplayCaptureState State,
    int TargetFrameRate,
    double InputFrameRate,
    double UniqueFrameRate,
    double OutputFrameRate,
    long DuplicateFrames,
    long DroppedFrames,
    int QueueDepth,
    string Encoder,
    string Adapter,
    string LastFailure,
    DateTime UpdatedUtc)
{
    public long TotalDroppedFrames { get; init; }
    public int PeakQueueDepth { get; init; }
    public DateTime? LastDegradedUtc { get; init; }
    // init property rather than a positional parameter: backends that can't
    // distinguish the two causes (and every existing construction site) keep
    // compiling unchanged and simply report None.
    public ReplayDegradeReason DegradeReason { get; init; }
    // What the encoder is actually running on, e.g. "NVIDIA GeForce RTX 4070 Ti".
    // Adapter above is the capture path's own label, not the hardware's name.
    public string AdapterDescription { get; init; } = string.Empty;
    // Encoder profile in force for this session. It is fixed when capture starts,
    // so consumers do not have to reach into settings that may have changed since.
    public string EncoderProfile { get; init; } = string.Empty;
    public int EncodeQueueCapacity { get; init; }
    // Whether this session emits real capture timing (VFR) or a duplicate-
    // padded fixed-rate timeline (CFR).
    public string FrameRateMode { get; init; } = string.Empty;
    // Whether a clip save was running while this sample was taken. A save runs
    // ffmpeg to build audio tracks and mux the result, which is a real but
    // brief and entirely self-inflicted load - the encode queue can back right
    // up for a second or two and recover the moment it finishes. Consumers that
    // react to sustained overload (EncoderTuningService) must not treat that as
    // evidence the machine cannot sustain its settings.
    public bool SaveInProgress { get; init; }
    public ReplayCaptureStartupPhase StartupPhase { get; init; }
    // The FPS selected in settings stays stable for the session while the
    // native backend may temporarily run a lower cadence to protect frames.
    // TargetFrameRate is the active cadence so existing consumers keep showing
    // the value that is actually being encoded.
    public int ConfiguredFrameRate { get; init; }
    public string EncoderInputPath { get; init; } = string.Empty;
    public bool FrameRateProtectionActive { get; init; }
    // Worker supervision fields. Optional init properties keep every local
    // backend and existing protocol payload source-compatible.
    public int RecoveryAttempt { get; init; }
    public int RecentWorkerFailureCount { get; init; }
    public int? LastWorkerExitCode { get; init; }
    public DateTime? NextWorkerRetryUtc { get; init; }
    public bool WorkerCrashLoopDetected { get; init; }
    public bool CapturePaused { get; init; }
    public ReplayRecoveryStopReason RecoveryStopReason { get; init; }
    public int? ProcessingGpuPriority { get; init; }
    public int? AcquisitionGpuPriority { get; init; }
    // Recovery fields are optional so old worker JSON payloads remain valid.
    // CleanSince is set only after two clean diagnostic windows.
    public DateTime? RecoveryCleanSinceUtc { get; init; }
    public ReplayPipelineRecoveryAction PipelineRecoveryAction { get; init; }
    // Vendor-neutral: submission latency/queue behaviour, never encoder name.
    public bool EncoderSubmissionStalled { get; init; }
    public ReplayPipelineStage BottleneckStage { get; init; }
    public long PacingMissedFrames { get; init; }
    public double PacingLatenessP95Ms { get; init; }
    public double PacingLatenessMaxMs { get; init; }
    public long EncodeQueueReplacements { get; init; }
    public double ProcessingP95Ms { get; init; }
    public double ProcessingMaxMs { get; init; }

    // Native engine diagnostics. These are optional for managed/legacy
    // backends, but make worker health actionable without parsing logs.
    public uint NativeEngineVersion { get; init; }
    public uint NativeBuildVersion { get; init; }
    public TimeSpan EncodeQueueAge { get; init; }
    public double EncoderSlotWaitP95Ms { get; init; }
    public double SubmissionP95Ms { get; init; }
    public double SubmissionMaxMs { get; init; }
    public double EncoderOutputLatencyP95Ms { get; init; }
    public double EncoderOutputLatencyMaxMs { get; init; }
    public int SurfacesInUse { get; init; }
    public int SurfaceCapacity { get; init; }
    public string AdapterLuid { get; init; } = string.Empty;
    public string FatalCategory { get; init; } = string.Empty;

    // WGC cadence remains separate from InputFrameRate so support bundles can
    // distinguish compositor cadence without parsing debug logs.
    public TimeSpan? WgcRequestedUpdateInterval { get; init; }
    public TimeSpan? WgcAppliedUpdateInterval { get; init; }

    // Producer-side DXGI measurements.  These deliberately do not reuse
    // UniqueFrameRate: that value describes frames that made it through the
    // consumer, whereas these values make a slow shared-texture transport
    // visible even while the encoder is otherwise healthy.
    public double AcquiredFrameRate { get; init; }
    public double TransportFrameRate { get; init; }
    public long TransportSlotOverwrites { get; init; }
    public long TransportBusySlotSkips { get; init; }
    public long TransportAllBusyDrops { get; init; }
    public long TransportReleaseLagFrames { get; init; }
    public TimeSpan ProducerGpuDuration { get; init; }
    public TimeSpan AverageTransportLeaseDuration { get; init; }
    // Cursor movement is a visual update even while desktop pixels stay still.
    // Keep it separate so source-content diagnostics remain truthful.
    public double PointerUpdateFrameRate { get; init; }

    public ReplayStorageHealth Storage { get; init; } = ReplayStorageHealth.Unknown;

    public static ReplayCaptureHealth Unknown(string backend = "Unknown") => new(
        backend, "Unknown", ReplayCaptureState.Unknown, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty, DateTime.UtcNow);
}

public interface IReplayCaptureDiagnostics
{
    ReplayCaptureHealth GetHealthSnapshot();
    event EventHandler<ReplayCaptureHealth>? HealthChanged;
}

public sealed record ReplaySaveCompleted(string Path, string? Title, DateTime CompletedUtc, string? Error = null);
public sealed record AutoClipDetectorEvent(string GameId, string EventId, string EventLabel, string OccurrenceId,
    double Confidence, DateTime TimestampUtc, int LeadSeconds, int TailSeconds);

public interface IReplayCaptureWorkerEvents
{
    event EventHandler? RecordingStateChanged;
    event EventHandler? SaveStarted;
    event EventHandler<ReplaySaveCompleted>? SaveCompleted;
    event EventHandler<bool>? FullSessionRecordingToggled;
    event EventHandler<AutoClipDetectorEvent>? AutoClipDetected;
}

public interface IReplayCaptureWorkerControl
{
    Task ShutdownWorkerAsync(CancellationToken cancellationToken = default);
    Task UpdateHotkeyAsync(string hotkey, CancellationToken cancellationToken = default);
    Task UpdateFullSessionHotkeyAsync(string hotkey, CancellationToken cancellationToken = default);
    Task UpdateClipGameNameAsync(string gameDisplayName, CancellationToken cancellationToken = default);
    Task UpdateAutoClipPolicyAsync(string? gameId, bool enabled, IReadOnlyList<string> enabledEventIds,
        CancellationToken cancellationToken = default);
}

public interface IStoragePressureObserver
{
    ReplayStorageHealth StorageHealth { get; }
    void RecordWrite(string path, TimeSpan elapsed);
    bool CanSave(int bitrateMbps, TimeSpan duration, out string reason);
}

// A backend that can be retargeted to a different frame rate without
// restarting - implemented only where that is genuinely free (the native
// engine, where it is purely a pacing change). Separate from
// IReplayCaptureDiagnostics because it is control, not observation, and a
// backend that cannot do it should not be forced to pretend.
public interface IAdaptiveCaptureFrameRate
{
    void RequestFrameRate(int frameRate);
}

public interface IReplayBuffer : IDisposable
{
    bool IsRecording { get; }
    TimeSpan Duration { get; }
    event EventHandler? RecordingStopped;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    // titleOverride, when set, replaces the default "{GameName} {timestamp}" clip
    // name entirely (e.g. "4K - Inferno") - used by auto-clip triggers (CS2 GSI
    // kill events) to name the clip after what just happened.
    Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null, string? gameDisplayNameOverride = null);
    void SetCapturePaused(bool paused) { }
    // Set by the most recent SaveReplayAsync when no genuinely new video frame
    // landed anywhere inside the saved window - i.e. the clip is one frozen
    // frame padded out to its full length. A capture source can stop delivering
    // frames while everything downstream keeps working, so without this the
    // first sign of it is opening the clip later and finding it black.
    // Backends that can't tell the difference simply never report it.
    bool LastSaveVideoWasFrozen => false;
}
