using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Auto prefers Native (ClypDat's own DXGI Desktop Duplication engine) -
// that's what the Settings description for "Auto (recommended)" has always
// promised ("Uses ClypDat's own capture engine for every game"), and it's
// the backend that's actually been proven reliable across every session.
public sealed class HybridReplayBuffer : IReplayBuffer, IReplayCaptureDiagnostics, IAdaptiveCaptureFrameRate
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private IReplayBuffer? _inner;
    private ReplayCaptureHealth _health = ReplayCaptureHealth.Unknown("Hybrid");
    private string _fallbackReason = string.Empty;

    public HybridReplayBuffer(Func<ReplayBufferConfig> configProvider) => _configProvider = configProvider;

    public bool IsRecording => _inner?.IsRecording == true;
    public TimeSpan Duration => _inner?.Duration ?? TimeSpan.Zero;
    public event EventHandler? RecordingStopped;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording) return;

        var config = _configProvider();
        try
        {
            var native = new NativeReplayBuffer(_configProvider);
            await StartInnerAsync(native, cancellationToken);
            _fallbackReason = string.Empty;
            SetHealth(new ReplayCaptureHealth("Hybrid", "Desktop Duplication", ReplayCaptureState.Starting,
                config.FrameRate, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty, DateTime.UtcNow));
            return;
        }
        catch (Exception error)
        {
            AppLog.Error("Hybrid capture: Native capture failed to start.", error);
            _fallbackReason = "Native capture unavailable.";
            SetHealth(new ReplayCaptureHealth("Hybrid", "Unavailable", ReplayCaptureState.Failed,
                config.FrameRate, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, _fallbackReason, DateTime.UtcNow));
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _inner?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    public Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null)
    {
        if (_inner is null) throw new InvalidOperationException("Replay buffer is not running.");
        return _inner.SaveReplayAsync(outputFolder, cancellationToken, titleOverride, clipWindow);
    }

    public void SetCapturePaused(bool paused) => _inner?.SetCapturePaused(paused);

    // Forwarded only when the backend actually running underneath supports it -
    // the legacy fallback does not, and silently doing nothing is the correct
    // answer there.
    public void RequestFrameRate(int frameRate)
    {
        if (_inner is IAdaptiveCaptureFrameRate adaptive) adaptive.RequestFrameRate(frameRate);
    }

    public bool LastSaveVideoWasFrozen => _inner?.LastSaveVideoWasFrozen == true;

    public ReplayCaptureHealth GetHealthSnapshot() => _inner is IReplayCaptureDiagnostics diagnostics
        ? ApplyFallbackReason(diagnostics.GetHealthSnapshot() with { Backend = "Hybrid" })
        : _health;

    private async Task StartInnerAsync(IReplayBuffer buffer, CancellationToken cancellationToken)
    {
        _inner = buffer;
        buffer.RecordingStopped += InnerStopped;
        if (buffer is IReplayCaptureDiagnostics diagnostics) diagnostics.HealthChanged += InnerHealthChanged;
        try
        {
            await buffer.StartAsync(cancellationToken);
        }
        catch
        {
            buffer.RecordingStopped -= InnerStopped;
            if (buffer is IReplayCaptureDiagnostics failedDiagnostics) failedDiagnostics.HealthChanged -= InnerHealthChanged;
            buffer.Dispose();
            _inner = null;
            throw;
        }
    }

    private void InnerStopped(object? sender, EventArgs args)
    {
        SetHealth(GetHealthSnapshot() with { State = ReplayCaptureState.Stopped, UpdatedUtc = DateTime.UtcNow });
        RecordingStopped?.Invoke(this, EventArgs.Empty);
    }

    private void InnerHealthChanged(object? sender, ReplayCaptureHealth health) => SetHealth(ApplyFallbackReason(health with { Backend = "Hybrid" }));

    private ReplayCaptureHealth ApplyFallbackReason(ReplayCaptureHealth health) =>
        string.IsNullOrWhiteSpace(health.LastFailure) && !string.IsNullOrWhiteSpace(_fallbackReason)
            ? health with { LastFailure = _fallbackReason }
            : health;

    private void SetHealth(ReplayCaptureHealth health)
    {
        _health = health;
        HealthChanged?.Invoke(this, health);
    }

    public void Dispose()
    {
        if (_inner is null) return;
        _inner.RecordingStopped -= InnerStopped;
        if (_inner is IReplayCaptureDiagnostics disposeDiagnostics) disposeDiagnostics.HealthChanged -= InnerHealthChanged;
        _inner.Dispose();
        _inner = null;
    }
}
