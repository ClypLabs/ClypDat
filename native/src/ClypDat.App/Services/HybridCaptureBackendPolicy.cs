namespace ClypDat.App.Services;

// DXGI Desktop Duplication is the native capture source for both games and
// desktops.  It is the only source whose input cadence is tied to the output
// being composed rather than the WGC frame-pool callback cadence.  The native
// replay buffer applies its privacy freeze when a game is not foreground so
// this does not expose whatever covers the window.
internal static class HybridCaptureBackendPolicy
{
    public static bool UseWgcForGame(string? _) => false;

    public static bool UseDxgiForDesktop(bool isMonitorMode) => true;
}
