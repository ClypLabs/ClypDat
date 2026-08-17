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
    Stopped
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
    CaptureStall
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
    // Encoder settings in force for this session, so a consumer can attribute
    // an overload to the preset that caused it without reaching into settings
    // (which the user may have changed since this buffer started).
    public string EncoderPreset { get; init; } = string.Empty;
    public int EncodeQueueCapacity { get; init; }
    // Whether a clip save was running while this sample was taken. A save runs
    // ffmpeg to build audio tracks and mux the result, which is a real but
    // brief and entirely self-inflicted load - the encode queue can back right
    // up for a second or two and recover the moment it finishes. Consumers that
    // react to sustained overload (EncoderTuningService) must not treat that as
    // evidence the machine cannot sustain its settings.
    public bool SaveInProgress { get; init; }

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

public interface IReplayCaptureWorkerEvents
{
    event EventHandler? RecordingStateChanged;
    event EventHandler? SaveStarted;
    event EventHandler<ReplaySaveCompleted>? SaveCompleted;
}

public interface IReplayCaptureWorkerControl
{
    Task ShutdownWorkerAsync(CancellationToken cancellationToken = default);
    Task UpdateHotkeyAsync(string hotkey, CancellationToken cancellationToken = default);
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
    Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null);
    void SetCapturePaused(bool paused) { }
    // Set by the most recent SaveReplayAsync when no genuinely new video frame
    // landed anywhere inside the saved window - i.e. the clip is one frozen
    // frame padded out to its full length. A capture source can stop delivering
    // frames while everything downstream keeps working, so without this the
    // first sign of it is opening the clip later and finding it black.
    // Backends that can't tell the difference simply never report it.
    bool LastSaveVideoWasFrozen => false;
}
