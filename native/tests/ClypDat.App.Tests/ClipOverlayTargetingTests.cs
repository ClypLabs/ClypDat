using Avalonia;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

// Notifications go where the user is playing: the detected game's monitor,
// else what replay captures, else the configured desktop monitor, and the
// primary display only when nothing better is known.
public sealed class ClipOverlayTargetingTests
{
    private static readonly ClipOverlayMonitor Primary = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 3840, 2160), new PixelRect(0, 0, 3840, 2100), 1.5);
    private static readonly ClipOverlayMonitor Side = new(@"\\.\DISPLAY2", new PixelRect(3840, 118, 1080, 1920), new PixelRect(3840, 118, 1080, 1863), 1.0);
    private static readonly ClipOverlayMonitor Third = new(@"\\.\DISPLAY3", new PixelRect(-1920, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1040), 1.25);

    [Fact]
    public void GameWindowWinsOverEverythingElse()
    {
        var monitors = new FakeMonitors { [11] = Side, [22] = Third };
        var target = ClipOverlayTargeting.Resolve(new ClipOverlayTargetHints(11, 22, Third.DeviceName, Primary.DeviceName), monitors);
        Assert.Equal(Side.DeviceName, target.DeviceName);
        Assert.Equal(ClipOverlayTargetReason.GameWindow, target.Reason);
        Assert.Equal(11, target.Window);
        // The badge is sized for the game's monitor, not the primary's.
        Assert.Equal(1.0, target.Scaling);
        Assert.Equal(Side.WorkArea, target.WorkArea);
        Assert.True(target.IsAuthoritative);
    }

    [Fact]
    public void CaptureWindowWhenNoGameIsDetected()
    {
        var monitors = new FakeMonitors { [22] = Third };
        var target = ClipOverlayTargeting.Resolve(new ClipOverlayTargetHints(CaptureWindow: 22, DesktopMonitorDeviceName: Side.DeviceName), monitors);
        Assert.Equal(Third.DeviceName, target.DeviceName);
        Assert.Equal(ClipOverlayTargetReason.CaptureWindow, target.Reason);
        Assert.Equal(1.25, target.Scaling);
        Assert.Equal(22, target.Window);
    }

    // A game window that closed or minimized is not a place to put a badge.
    [Fact]
    public void GoneGameWindowFallsThroughToCaptureMonitor()
    {
        var monitors = new FakeMonitors();
        var target = ClipOverlayTargeting.Resolve(new ClipOverlayTargetHints(GameWindow: 99, CaptureMonitorDeviceName: Side.DeviceName), monitors);
        Assert.Equal(Side.DeviceName, target.DeviceName);
        Assert.Equal(ClipOverlayTargetReason.CaptureMonitor, target.Reason);
        Assert.Equal(0, target.Window);
    }

    [Fact]
    public void DesktopMonitorSettingBeforePrimary()
    {
        var target = ClipOverlayTargeting.Resolve(new ClipOverlayTargetHints(DesktopMonitorDeviceName: Third.DeviceName), new FakeMonitors());
        Assert.Equal(Third.DeviceName, target.DeviceName);
        Assert.Equal(ClipOverlayTargetReason.DesktopMonitor, target.Reason);
    }

    [Fact]
    public void PrimaryOnlyAsTheLastResort()
    {
        var target = ClipOverlayTargeting.Resolve(new ClipOverlayTargetHints(DesktopMonitorDeviceName: @"\\.\DISPLAY9"), new FakeMonitors());
        Assert.Equal(Primary.DeviceName, target.DeviceName);
        Assert.Equal(ClipOverlayTargetReason.Primary, target.Reason);
        Assert.Equal("primary", target.ReasonLabel);
        Assert.False(target.IsAuthoritative);
    }

    // Placement and card size follow each monitor's own DPI and work area,
    // including negative coordinates left of the primary.
    [Fact]
    public void LayoutFollowsEachMonitorsDpiAndWorkArea()
    {
        foreach (var monitor in new[] { Primary, Side, Third })
        {
            var target = ClipOverlayTargeting.Resolve(new ClipOverlayTargetHints(GameWindow: 5), new FakeMonitors { [5] = monitor });
            var width = (int)Math.Round(330 * target.Scaling);
            var height = (int)Math.Round(87 * target.Scaling);
            var frame = ClipOverlayLayout.Frame(target, ClipOverlayPlacement.TopRight, width, height);
            Assert.Equal(monitor.WorkArea.Right, frame.Window.Right);
            Assert.InRange(frame.Window.X, monitor.WorkArea.X, monitor.WorkArea.Right - width);
            Assert.Equal(monitor.WorkArea.Y + (int)Math.Round(32 * monitor.Scaling), frame.Window.Y);
        }
    }

    // The real resolver on this machine: with no hints it is the primary
    // display Windows reports.
    [Fact]
    public void RealResolverFallsBackToTheWindowsPrimary()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = ClipOverlayTargeting.Resolve(default);
        Assert.Equal(ClipOverlayTargetReason.Primary, target.Reason);
        Assert.StartsWith(@"\\.\DISPLAY", target.DeviceName);
        Assert.Equal(0, target.Bounds.X);
        Assert.Equal(0, target.Bounds.Y);
    }

    private sealed class FakeMonitors : Dictionary<nint, ClipOverlayMonitor>, IClipOverlayMonitors
    {
        public ClipOverlayMonitor? FromWindow(nint window) => TryGetValue(window, out var monitor) ? monitor : null;
        public ClipOverlayMonitor? FromDeviceName(string deviceName)
            => new[] { ClipOverlayTargetingTests.Primary, Side, Third }.Where(monitor => monitor.DeviceName == deviceName).Select(monitor => (ClipOverlayMonitor?)monitor).FirstOrDefault();
        public ClipOverlayMonitor Primary() => ClipOverlayTargetingTests.Primary;
    }
}
