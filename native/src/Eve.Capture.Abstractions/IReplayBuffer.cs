namespace Eve.Capture.Abstractions;

public interface IReplayBuffer
{
    bool IsRecording { get; }
    TimeSpan Duration { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default);
}
