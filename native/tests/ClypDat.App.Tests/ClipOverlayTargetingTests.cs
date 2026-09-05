using Avalonia;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipOverlayTargetingTests
{
    [Fact]
    public void ResolvePrimaryMatchesWindowsPrimaryDisplay()
    {
        if (!OperatingSystem.IsWindows()) return;
        var expected = DesktopMonitorService.GetMonitors().Single(monitor => monitor.IsPrimary);

        var target = ClipOverlayTargeting.ResolvePrimary();

        Assert.Equal(expected.DeviceName, target.DeviceName, ignoreCase: true);
        Assert.Equal(new PixelRect(expected.X, expected.Y, expected.Width, expected.Height), target.Bounds);
        Assert.Equal(ClipOverlayTargetReason.Primary, target.Reason);
        Assert.InRange(target.WorkArea.X, target.Bounds.X, target.Bounds.Right);
        Assert.InRange(target.WorkArea.Y, target.Bounds.Y, target.Bounds.Bottom);
        Assert.InRange(target.WorkArea.Right, target.Bounds.X, target.Bounds.Right);
        Assert.InRange(target.WorkArea.Bottom, target.Bounds.Y, target.Bounds.Bottom);
        Assert.True(target.Scaling > 0);
    }
}
