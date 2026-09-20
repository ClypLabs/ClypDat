namespace ClypDat.App.Services;

// Whether a capture session should describe itself as paused, which suppresses
// the capture-health watchdog and replaces the live capture metrics in the UI.
// It is a statement about the SOURCE, not about the user's attention: the only
// question that matters is whether the active source still has content to give.
internal static class CapturePausePolicy
{
    /// <param name="hostRequestedPause">
    /// The host's background-game pause. It means "the detected game is not in
    /// the foreground", which is a statement about a desktop-composition source.
    /// </param>
    public static bool IsPaused(
        bool hostRequestedPause,
        bool isMonitorMode,
        bool usingWindowGraphicsCapture,
        bool targetForeground,
        bool targetCapturable)
    {
        // Desktop capture has content whatever the user is looking at, so only an
        // explicit host pause stops it.
        if (isMonitorMode) return hostRequestedPause;
        // WGC captures the window itself and keeps producing frames while it sits
        // behind another window, so being backgrounded is not a pause. A minimised
        // or closed window has genuinely stopped producing.
        if (usingWindowGraphicsCapture) return !targetCapturable;
        // DXGI Desktop Duplication reads the composed desktop, where a background
        // game contributes nothing.
        return hostRequestedPause || !targetForeground;
    }
}
