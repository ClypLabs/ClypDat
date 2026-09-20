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

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    public void DeliveryFloor_TargetNearTheDisplayRate_DoesNotThrottle(int frameRate)
    {
        // The 4K240 trace this was written for: a 90 FPS target asked for 6.25ms,
        // which rounds up to two ticks - an 8.333ms floor and a 120 FPS ceiling,
        // landing exactly on the present rate of a 120 FPS game. Presents that ran
        // a fraction early waited a whole extra tick, and the source read 100-110.
        Assert.Equal(1000d / 240, FloorMs(frameRate, 240), 3);
    }

    [Fact]
    public void DeliveryFloor_TargetWellUnderTheDisplayRate_KeepsThrottling()
    {
        // 60 FPS on a 240Hz display: a 120 FPS ceiling is still double the target,
        // so an uncapped source is halved instead of copying every composition.
        Assert.Equal(2 * 1000d / 240, FloorMs(60, 240), 3);
    }

    [Fact]
    public void DeliveryFloor_LowTarget_ThrottlesHard()
    {
        // 30 FPS on 240Hz: five ticks, a 48 FPS ceiling.
        Assert.Equal(5 * 1000d / 240, FloorMs(30, 240), 3);
    }

    [Fact]
    public void DeliveryFloor_DisplayCannotOutrunTheTarget_UsesASingleTick()
    {
        Assert.Equal(1000d / 60, FloorMs(60, 60), 3);
        Assert.Equal(1000d / 60, FloorMs(90, 60), 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void FromFrameRate_UnknownRefresh_AsksForHalfTheFramePeriod(double refreshHz)
    {
        // No grid to reason about, so under-ask: whatever this rounds up to on the
        // real grid still cannot delay a source running at the target rate.
        var interval = WgcMinimumUpdateIntervalPolicy.FromFrameRate(90, refreshHz);

        Assert.Equal(1000d / 90 / 2, interval.TotalMilliseconds, 3);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(60, 120)]
    [InlineData(60, 144)]
    [InlineData(60, 240)]
    [InlineData(90, 144)]
    [InlineData(90, 165)]
    [InlineData(90, 240)]
    [InlineData(120, 240)]
    [InlineData(30, 60)]
    [InlineData(30, 240)]
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
    public void DeliveryFloor_SameTargetOnADifferentDisplay_NeedsRederiving()
    {
        // Why the interval is re-derived when a captured window moves between
        // displays: the 60Hz answer, applied unchanged on a 240Hz panel, rounds
        // up to two ticks - the 120 FPS ceiling this policy exists to avoid.
        var sixtyHzRequest = WgcMinimumUpdateIntervalPolicy.FromFrameRate(90, 60);

        Assert.Equal(2 * 1000d / 240, WgcMinimumUpdateIntervalPolicy.DeliveryFloor(sixtyHzRequest, 240).TotalMilliseconds, 3);
        Assert.Equal(1000d / 240, FloorMs(90, 240), 3);
    }

    [Fact]
    public void DeliveryFloor_UnknownRefresh_LeavesTheRequestAlone()
    {
        var requested = TimeSpan.FromMilliseconds(5);

        Assert.Equal(requested, WgcMinimumUpdateIntervalPolicy.DeliveryFloor(requested, 0));
    }
}
