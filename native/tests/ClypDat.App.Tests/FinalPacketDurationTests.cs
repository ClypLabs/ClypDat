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

    [Theory]
    [InlineData(true)]
    public void FinalPacketDuration_SinglePacketWindow_IsNeverZero(bool variableFrameTiming)
    {
        // A one-packet window leaves no preceding cadence to read.
        var duration = ReplayFrameTimingPolicy.FinalPacketDurationMicroseconds(
            variableFrameTiming, previousDurationMicroseconds: 0, holdMicroseconds: 0);

        Assert.True(duration >= 1);
    }
}
