using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal static class UiPreviewMode
{
#if CLYPDAT_UI_PREVIEW
    public static bool Enabled => true;
#else
    public static bool Enabled => false;
#endif
}

#if CLYPDAT_UI_PREVIEW
// The macOS preview deliberately has no recording backend. The real capture
// implementations stay excluded from the preview target, while the existing
// window and view model can still exercise their normal state and bindings.
internal sealed class UiPreviewReplayBuffer : IReplayBuffer, IReplayCaptureDiagnostics
{
    public bool IsRecording => false;
    public TimeSpan Duration => TimeSpan.Zero;
    public event EventHandler? RecordingStopped;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;

    public ReplayCaptureHealth GetHealthSnapshot() => ReplayCaptureHealth.Unknown("UI preview");

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> SaveReplayAsync(
        string outputFolder,
        CancellationToken cancellationToken = default,
        string? titleOverride = null,
        ReplayClipWindow? clipWindow = null) => Task.FromResult(string.Empty);

    public void Dispose()
    {
        RecordingStopped = null;
        HealthChanged = null;
    }
}

#endif
