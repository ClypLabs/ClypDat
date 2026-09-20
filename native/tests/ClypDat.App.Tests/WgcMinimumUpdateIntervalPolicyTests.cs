using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class WgcMinimumUpdateIntervalPolicyTests
{
    // The 4K240 trace this was written for: a 90 FPS target on a 240Hz display
    // was asking for 11.111ms, which WGC rounds up to the third composition
    // tick (12.5ms) and caps the source at 80 FPS.
    [Fact]
    public void FromFrameRate_TargetBetweenRefreshTicks_RequestsTheLowerTick()
    {
        var interval = WgcMinimumUpdateIntervalPolicy.FromFrameRate(90, 240);

        // Two refresh periods (8.333ms) minus half a period of jitter margin.
        Assert.Equal(6.25, interval.TotalMilliseconds, 3);
        Assert.True(interval.TotalMilliseconds < 1000d / 90);
    }

    [Fact]
    public void FromFrameRate_TargetOnARefreshTick_StillLeavesJitterMargin()
    {
        var interval = WgcMinimumUpdateIntervalPolicy.FromFrameRate(60, 120);

        // 60 FPS is exactly every second tick at 120Hz: request 1.5 ticks so a
        // frame arriving fractionally early is not pushed to the third.
        Assert.Equal(12.5, interval.TotalMilliseconds, 3);
    }

    [Fact]
    public void FromFrameRate_RefreshMatchesTarget_RequestsHalfAPeriod()
    {
        var interval = WgcMinimumUpdateIntervalPolicy.FromFrameRate(60, 60);

        Assert.Equal(8.333, interval.TotalMilliseconds, 3);
    }

    [Fact]
    public void FromFrameRate_DisplaySlowerThanTarget_DoesNotThrottleBelowTheDisplay()
    {
        var interval = WgcMinimumUpdateIntervalPolicy.FromFrameRate(90, 60);

        // Nothing can reach 90 FPS here; never ask for an interval that would
        // also drop the 60 the display can actually produce.
        Assert.Equal(8.333, interval.TotalMilliseconds, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void FromFrameRate_UnknownRefresh_FallsBackToTheFramePeriod(double refreshHz)
    {
        var interval = WgcMinimumUpdateIntervalPolicy.FromFrameRate(90, refreshHz);

        Assert.Equal(1000d / 90, interval.TotalMilliseconds, 3);
    }

    [Theory]
    [InlineData(60, 144)]
    [InlineData(90, 144)]
    [InlineData(90, 165)]
    [InlineData(120, 240)]
    [InlineData(240, 240)]
    [InlineData(30, 60)]
    public void FromFrameRate_AnyDisplay_AllowsAtLeastTheTargetRate(int frameRate, double refreshHz)
    {
        var interval = WgcMinimumUpdateIntervalPolicy.FromFrameRate(frameRate, refreshHz);

        // What the interval quantises to on this display's grid must still leave
        // room for the target frame rate, which is the bug this policy fixes.
        var refreshPeriodMs = 1000d / refreshHz;
        var deliveredPeriodMs = Math.Ceiling(interval.TotalMilliseconds / refreshPeriodMs - 1e-6) * refreshPeriodMs;
        var deliverableRate = 1000d / deliveredPeriodMs;

        var target = Math.Clamp(frameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate);
        Assert.True(deliverableRate >= Math.Min(target, refreshHz) - 0.001,
            $"{frameRate} FPS on {refreshHz}Hz delivers only {deliverableRate:0.00} FPS.");
    }
}
