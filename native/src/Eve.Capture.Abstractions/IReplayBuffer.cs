namespace Eve.Capture.Abstractions;

public interface IReplayBuffer : IDisposable
{
    bool IsRecording { get; }
    TimeSpan Duration { get; }
    event EventHandler? RecordingStopped;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default);
    void SetCapturePaused(bool paused) { }
}
