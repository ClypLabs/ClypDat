using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CapturePausePolicyTests
{
    [Fact]
    public void IsPaused_WgcWindowBehindAnotherWindow_KeepsReporting()
    {
        // Alt-tabbed out of the game: WGC is still delivering that window's
        // frames, so the metrics must stay live instead of being replaced by a
        // "replay paused while game is backgrounded" notice.
        Assert.False(CapturePausePolicy.IsPaused(
            hostRequestedPause: true, isMonitorMode: false, usingWindowGraphicsCapture: true,
            targetForeground: false, targetCapturable: true));
    }

    [Fact]
    public void IsPaused_DesktopCapture_OnlyTheHostPauses()
    {
        Assert.False(CapturePausePolicy.IsPaused(
            hostRequestedPause: false, isMonitorMode: true, usingWindowGraphicsCapture: false,
            targetForeground: false, targetCapturable: false));
        Assert.True(CapturePausePolicy.IsPaused(
            hostRequestedPause: true, isMonitorMode: true, usingWindowGraphicsCapture: false,
            targetForeground: true, targetCapturable: true));
    }
}
