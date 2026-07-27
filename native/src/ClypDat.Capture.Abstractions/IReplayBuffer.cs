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
    public static ReplayCaptureHealth Unknown(string backend = "Unknown") => new(
        backend, "Unknown", ReplayCaptureState.Unknown, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty, DateTime.UtcNow);
}

public interface IReplayCaptureDiagnostics
{
    ReplayCaptureHealth GetHealthSnapshot();
    event EventHandler<ReplayCaptureHealth>? HealthChanged;
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
}
