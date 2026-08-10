namespace ClypDat.App.Services;

// ScreenRecorderLib's WGC recorder leaks native handles and worker threads when
// a live replay is rotated. At the old 20-second rotation cadence that leak was
// measured at several GB per hour. Native DXGI uses one bounded ring and already
// crops the target HWND while freezing safely when it is not foreground, so it
// is the production backend for game capture.
//
// WGC remains available for short diagnostic comparisons only. It is deliberately
// an environment opt-in rather than a user setting: opting into an unbounded
// native leak must never be required for normal recording.
internal static class HybridCaptureBackendPolicy
{
    internal const string WgcGameOptInVariable = "CLYPDAT_ENABLE_WGC_GAME";

    public static bool UseWgcForGame(string? optInValue) =>
        string.Equals(optInValue, "1", StringComparison.Ordinal);
}
