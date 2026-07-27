using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Auto must prefer Present-hook capture for games that allow it, but never make
// recording depend on injection succeeding. Full sessions keep Native for now:
// its mux/finalize path is the only implementation with feature parity.
public sealed class HybridReplayBuffer : IReplayBuffer, IReplayCaptureDiagnostics
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
        if (config.FullSessionRecordingEnabled)
        {
            await StartNativeAsync("Full-session recording uses Native until OBS session output reaches parity.", cancellationToken);
            return;
        }

        if (ObsRuntimeLocator.IsAvailable(out _, out var reason))
        {
            try
            {
                var obs = new ObsReplayBuffer(_configProvider);
                await StartInnerAsync(obs, cancellationToken);
                _fallbackReason = string.Empty;
                SetHealth(new ReplayCaptureHealth("Hybrid", "Game hook", ReplayCaptureState.Starting,
                    config.FrameRate, 0, 0, 0, 0, 0, 0, "OBS", string.Empty,
                    "Waiting for game hook frames.", DateTime.UtcNow));
                return;
            }
            catch (Exception error)
            {
                AppLog.Error("Hybrid capture: OBS game hook unavailable, falling back to Native.", error);
            }
        }
        else
        {
            AppLog.Info($"Hybrid capture: OBS unavailable, falling back to Native. {reason}");
        }

        await StartNativeAsync("Game hook unavailable. Using Desktop Duplication.", cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _inner?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    public Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null)
    {
        if (_inner is null) throw new InvalidOperationException("Replay buffer is not running.");
        return _inner.SaveReplayAsync(outputFolder, cancellationToken, titleOverride, clipWindow);
    }

    public void SetCapturePaused(bool paused) => _inner?.SetCapturePaused(paused);

    public ReplayCaptureHealth GetHealthSnapshot() => _inner is IReplayCaptureDiagnostics diagnostics
        ? ApplyFallbackReason(diagnostics.GetHealthSnapshot() with { Backend = "Hybrid" })
        : _health;

    private async Task StartNativeAsync(string reason, CancellationToken cancellationToken)
    {
        _fallbackReason = reason;
        var config = _configProvider();
        var native = new NativeReplayBuffer(_configProvider);
        await StartInnerAsync(native, cancellationToken);
        SetHealth(new ReplayCaptureHealth("Hybrid", "Desktop Duplication", ReplayCaptureState.Degraded,
            config.FrameRate, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, reason, DateTime.UtcNow));
    }

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
