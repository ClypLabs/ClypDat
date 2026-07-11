namespace Eve.Capture.Abstractions;

public interface IReplayBuffer : IDisposable
{
    bool IsRecording { get; }
    TimeSpan Duration { get; }
    event EventHandler? RecordingStopped;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    // titleSuffix appends to the saved clip's file name (e.g. "Ace", "Headshot Kill")
    // - used by auto-clip triggers (CS2 GSI kill events) to name the clip after what
    // just happened instead of a bare game-name-plus-timestamp.
    Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleSuffix = null);
    void SetCapturePaused(bool paused) { }
}
