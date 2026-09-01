using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DxgiCadenceFallbackPolicyTests
{
    [Fact]
    public void ShouldFallback_PersistentlyLowFreshFrameRate_SwitchesAfterWarmup()
    {
        var policy = new DxgiCadenceFallbackPolicy();

        // Captured CS2 trace: target=90 FPS, fresh=2.49 FPS, no queue pressure.
        Assert.False(policy.ShouldFallback(90, 2.49, foregroundAndVisible: true, encoderPressure: false));
        Assert.False(policy.ShouldFallback(90, 2.49, foregroundAndVisible: true, encoderPressure: false));
        Assert.False(policy.ShouldFallback(90, 2.49, foregroundAndVisible: true, encoderPressure: false));
        Assert.True(policy.ShouldFallback(90, 2.49, foregroundAndVisible: true, encoderPressure: false));
        policy.MarkFallbackCommitted();
        Assert.False(policy.ShouldFallback(90, 2.49, foregroundAndVisible: true, encoderPressure: false));
    }

    [Fact]
    public void ShouldFallback_EncoderPressure_DoesNotBlameDxgi()
    {
        var policy = new DxgiCadenceFallbackPolicy();

        for (var sample = 0; sample < 6; sample++)
            Assert.False(policy.ShouldFallback(90, 2.49, foregroundAndVisible: true, encoderPressure: true));
    }
}
