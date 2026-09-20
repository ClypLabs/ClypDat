using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FinalPacketDurationTests
{
    private const long FrameAt90Fps = 11_111;

    [Fact]
    public void FinalPacketDuration_VariableTiming_ClampsALongHoldToTwoFrames()
    {
        // Measured on a 90 FPS VFR save: the ring's newest frame was 485ms old,
        // so the clip ended on a half-second freeze and reported a duration
        // longer than the motion in it.
        var duration = ReplayFrameTimingPolicy.FinalPacketDurationMicroseconds(
            variableFrameTiming: true, previousDurationMicroseconds: FrameAt90Fps, holdMicroseconds: 485_090);

        Assert.Equal(FrameAt90Fps * ReplayFrameTimingPolicy.MaximumFinalHoldFrames, duration);
    }

    [Fact]
    public void FinalPacketDuration_VariableTiming_KeepsAShortHold()
    {
        // The sub-frame gap the hold exists for: save landing partway through
        // the last frame's interval.
        var duration = ReplayFrameTimingPolicy.FinalPacketDurationMicroseconds(
            variableFrameTiming: true, previousDurationMicroseconds: FrameAt90Fps, holdMicroseconds: 7_400);

        Assert.Equal(7_400, duration);
    }

    [Fact]
    public void FinalPacketDuration_ConstantTiming_KeepsItsCadence()
    {
        var duration = ReplayFrameTimingPolicy.FinalPacketDurationMicroseconds(
            variableFrameTiming: false, previousDurationMicroseconds: FrameAt90Fps, holdMicroseconds: 485_090);

        Assert.Equal(FrameAt90Fps, duration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinalPacketDuration_SinglePacketWindow_IsNeverZero(bool variableFrameTiming)
    {
        // A one-packet window leaves no preceding cadence to read.
        var duration = ReplayFrameTimingPolicy.FinalPacketDurationMicroseconds(
            variableFrameTiming, previousDurationMicroseconds: 0, holdMicroseconds: 0);

        Assert.True(duration >= 1);
    }
}
