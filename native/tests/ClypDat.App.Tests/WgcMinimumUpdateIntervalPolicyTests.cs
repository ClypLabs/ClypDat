using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class WgcMinimumUpdateIntervalPolicyTests
{
    // WGC releases frames on composition ticks, so what it enforces is the
    // request rounded up to a whole tick. Every case below is about that
    // rounded value, not the request.
    private static double FloorMs(int frameRate, double refreshHz) =>
        WgcMinimumUpdateIntervalPolicy.DeliveryFloor(
            WgcMinimumUpdateIntervalPolicy.FromFrameRate(frameRate, refreshHz), refreshHz).TotalMilliseconds;

    [Fact]
    public void DeliveryFloor_LowTarget_ThrottlesHard()
    {
        // 30 FPS on 240Hz: five ticks, a 48 FPS ceiling.
        Assert.Equal(5 * 1000d / 240, FloorMs(30, 240), 3);
    }

    [Theory]
    [InlineData(60, 60)]
    public void DeliveryFloor_AnyDisplay_NeverDelaysASourceRunningAboveTheTarget(int frameRate, double refreshHz)
    {
        var floorMs = FloorMs(frameRate, refreshHz);
        var targetPeriodMs = 1000d / frameRate;

        // A source presenting a third faster than the target - the common case of
        // a 120 FPS game recorded at 90 - must never be pushed onto a later tick.
        var sourcePeriodMs = targetPeriodMs / 1.33;
        var deliverable = Math.Min(refreshHz, 1000d / Math.Max(floorMs, sourcePeriodMs));

        Assert.True(deliverable >= Math.Min(frameRate, refreshHz) - 0.001,
            $"{frameRate} FPS on {refreshHz}Hz leaves a {1000d / floorMs:0.0} FPS ceiling, delivering {deliverable:0.0}.");
        Assert.True(floorMs <= targetPeriodMs + 0.001,
            $"{frameRate} FPS on {refreshHz}Hz enforces {floorMs:0.###}ms, longer than the {targetPeriodMs:0.###}ms frame period.");
    }

    [Fact]
    public void DeliveryFloor_UnknownRefresh_LeavesTheRequestAlone()
    {
        var requested = TimeSpan.FromMilliseconds(5);

        Assert.Equal(requested, WgcMinimumUpdateIntervalPolicy.DeliveryFloor(requested, 0));
    }
}
