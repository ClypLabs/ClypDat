namespace Eve.Capture.Abstractions;

// An event-derived section of the replay timeline. Manual saves leave this null
// and continue to use the user's configured replay duration.
public sealed record ReplayClipWindow(DateTime StartUtc, DateTime EndUtc);

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
