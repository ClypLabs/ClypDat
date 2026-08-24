using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplayFrameRateDisplaySmootherTests
{
    [Fact]
    public void Update_InitializesThenRejectsOneSampleOutlier()
    {
        var smoother = new ReplayFrameRateDisplaySmoother();

        Assert.Equal((90d, 90d), smoother.Update(Health(90, 90)));
        Assert.Equal((90d, 90d), smoother.Update(Health(30, 30)));
        Assert.Equal((90d, 90d), smoother.Update(Health(90, 90)));
    }

    [Fact]
    public void Update_UsesRollingMedianForBothDisplayedRates()
    {
        var smoother = new ReplayFrameRateDisplaySmoother();

        smoother.Update(Health(90, 88));
        smoother.Update(Health(86, 90));
        var rates = smoother.Update(Health(88, 86));

        Assert.Equal((88d, 88d), rates);
    }

    [Fact]
    public void Update_ConfigurationChangeClearsRateHistory()
    {
        var smoother = new ReplayFrameRateDisplaySmoother();

        smoother.Update(Health(90, 90, encoder: "h264_nvenc", targetRate: 90));
        smoother.Update(Health(30, 30, encoder: "h264_nvenc", targetRate: 90));
        var rates = smoother.Update(Health(60, 60, encoder: "h264_amf", targetRate: 60));

        Assert.Equal((60d, 60d), rates);
    }

    [Fact]
    public void Update_TimingModeChangeClearsRateHistory()
    {
        var smoother = new ReplayFrameRateDisplaySmoother();

        smoother.Update(Health(90, 90));
        smoother.Update(Health(30, 30));
        var rates = smoother.Update(Health(60, 60, timingMode: ReplayFrameTimingPolicy.Variable));

        Assert.Equal((60d, 60d), rates);
    }

    [Fact]
    public void Reset_ClearsTheSessionHistory()
    {
        var smoother = new ReplayFrameRateDisplaySmoother();

        smoother.Update(Health(90, 90));
        smoother.Update(Health(30, 30));
        smoother.Reset();

        Assert.Equal((60d, 60d), smoother.Update(Health(60, 60)));
    }

    [Theory]
    [InlineData(ReplayCaptureState.Healthy, ReplayDegradeReason.None)]
    [InlineData(ReplayCaptureState.Degraded, ReplayDegradeReason.CaptureStall)]
    [InlineData(ReplayCaptureState.Stopped, ReplayDegradeReason.None)]
    public void Update_ClearsUniqueRateImmediatelyWhenCaptureHasNoFreshFrames(ReplayCaptureState state, ReplayDegradeReason reason)
    {
        var smoother = new ReplayFrameRateDisplaySmoother();
        smoother.Update(Health(90, 90));

        var rates = smoother.Update(Health(90, state == ReplayCaptureState.Healthy ? 0 : 90, state: state, degradeReason: reason));

        Assert.Equal(90d, rates.OutputFrameRate);
        Assert.Equal(0d, rates.UniqueFrameRate);
    }

    private static ReplayCaptureHealth Health(
        double output,
        double unique,
        string encoder = "h264_nvenc",
        int targetRate = 90,
        string timingMode = ReplayFrameTimingPolicy.Constant,
        ReplayCaptureState state = ReplayCaptureState.Healthy,
        ReplayDegradeReason degradeReason = ReplayDegradeReason.None) =>
        new("Native", "Desktop Duplication", state, targetRate, unique, unique, output, 0, 0, 0,
            encoder, "Adapter", string.Empty, DateTime.UtcNow)
        {
            FrameRateMode = timingMode,
            DegradeReason = degradeReason
        };
}
