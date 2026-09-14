using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Fail closed until the private recorder's full KDE contract is implemented.
// The old non-Windows FFmpeg backend silently records a different source.
internal sealed class LinuxReplayBuffer : IReplayBuffer, IReplayCaptureDiagnostics, IReplayBackendReadiness
{
    private const string Reason = "Linux capture is unavailable: private KDE recorder integration is not ready.";
    public bool IsRecording => false;
    public TimeSpan Duration => TimeSpan.Zero;
    public event EventHandler? RecordingStopped;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;
    public ReplayBackendReadiness GetReadiness() => new(false, ReplayBackendCapabilities.None, Reason);
    public ReplayCaptureHealth GetHealthSnapshot() => ReplayCaptureHealth.Unknown("KDE / GSR") with
    {
        State = ReplayCaptureState.Failed, LastFailure = Reason
    };
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HealthChanged?.Invoke(this, GetHealthSnapshot());
        return Task.FromException(new PlatformNotSupportedException(Reason));
    }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        RecordingStopped?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
    public Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default,
        string? titleOverride = null, ReplayClipWindow? clipWindow = null,
        string? gameDisplayNameOverride = null, Guid? saveId = null) =>
        Task.FromException<string>(new PlatformNotSupportedException(Reason));
    public void Dispose() { RecordingStopped = null; HealthChanged = null; }
}
