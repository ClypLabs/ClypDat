namespace ClypDat.App.Services;

internal static class OverlayRecordingModeRestart
{
    // Burned or editable overlay layers are fixed for a recording session by
    // the worker. A change while replay records restarts replay so the new
    // mode applies now rather than silently at the next start.
    internal static bool Required(bool recording, bool transitioning, string? active, string requested) =>
        recording && !transitioning && active is not null && !string.Equals(active, requested, StringComparison.Ordinal);
}
