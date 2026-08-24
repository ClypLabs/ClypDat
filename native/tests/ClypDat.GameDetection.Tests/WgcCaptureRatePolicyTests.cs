using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class WgcCaptureRatePolicyTests
{
    [Fact]
    public void MinimumUpdateInterval_ConvertsNinetyFpsToElevenPointOneMilliseconds()
    {
        var interval = WgcMinimumUpdateIntervalPolicy.FromFrameRate(90);

        Assert.Equal(TimeSpan.FromSeconds(1d / 90d), interval);
    }

    [Fact]
    public void UnsupportedMinimumUpdateInterval_HasNoAppliedValue()
    {
        var result = WgcMinimumUpdateIntervalPolicy.Unsupported(90);

        Assert.False(result.InterfaceAvailable);
        Assert.Null(result.Applied);
        Assert.Equal(TimeSpan.FromSeconds(1d / 90d), result.Requested);
    }

    [Fact]
    public void NinetyFpsWithSixtyCallbacks_FallsBackAfterWarmupAndThreeLowWindows()
    {
        var policy = new WgcCadenceFallbackPolicy();

        Assert.False(policy.ShouldFallback(90, 60, foregroundAndVisible: true, encoderPressure: false));
        Assert.False(policy.ShouldFallback(90, 60, foregroundAndVisible: true, encoderPressure: false));
        Assert.False(policy.ShouldFallback(90, 60, foregroundAndVisible: true, encoderPressure: false));
        Assert.True(policy.ShouldFallback(90, 60, foregroundAndVisible: true, encoderPressure: false));
    }

    [Theory]
    [InlineData(90, 81)]
    [InlineData(60, 60)]
    [InlineData(30, 30)]
    public void CadenceAtThresholdOrTarget_DoesNotFallback(int target, double callbacks)
    {
        var policy = new WgcCadenceFallbackPolicy();

        for (var index = 0; index < 8; index++)
            Assert.False(policy.ShouldFallback(target, callbacks, foregroundAndVisible: true, encoderPressure: false));
    }

    [Fact]
    public void BackgroundResizeOverloadAndFrameRateChanges_ResetCadenceProbe()
    {
        var policy = new WgcCadenceFallbackPolicy();

        Assert.False(policy.ShouldFallback(90, 60, foregroundAndVisible: true, encoderPressure: false));
        Assert.False(policy.ShouldFallback(90, 60, foregroundAndVisible: true, encoderPressure: false));
        Assert.False(policy.ShouldFallback(90, 60, foregroundAndVisible: false, encoderPressure: false));
        Assert.False(policy.ShouldFallback(90, 60, foregroundAndVisible: true, encoderPressure: false));

        policy.Reset(); // resize or target change
        Assert.False(policy.ShouldFallback(90, 60, foregroundAndVisible: true, encoderPressure: false));

        Assert.False(policy.ShouldFallback(90, 60, foregroundAndVisible: true, encoderPressure: true));
        Assert.False(policy.ShouldFallback(60, 40, foregroundAndVisible: true, encoderPressure: false));
        Assert.False(policy.ShouldFallback(60, 40, foregroundAndVisible: true, encoderPressure: false));
    }
}
