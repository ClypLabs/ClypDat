using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplayFrameTimingPolicyTests
{
    [Theory]
    [InlineData(null, ReplayFrameTimingPolicy.Variable)]
    [InlineData("vfr", ReplayFrameTimingPolicy.Variable)]
    [InlineData("CFR", ReplayFrameTimingPolicy.Constant)]
    [InlineData("anything else", ReplayFrameTimingPolicy.Variable)]
    public void NormalizesPersistedTimingMode(string? value, string expected)
    {
        Assert.Equal(expected, ReplayFrameTimingPolicy.Normalize(value));
    }

    [Theory]
    [InlineData(30, 4)]
    [InlineData(60, 8)]
    [InlineData(90, 12)]
    [InlineData(120, 15)]
    [InlineData(144, 18)]
    public void BoundsQueueToAbout125Milliseconds(int frameRate, int expectedCapacity)
    {
        Assert.Equal(expectedCapacity, ReplayFrameTimingPolicy.EncodeQueueCapacity(frameRate));
    }

    [Fact]
    public void RealPtsFollowsCaptureClockAndRemainsMonotonic()
    {
        var first = ReplayFrameTimingPolicy.RealPtsMicroseconds(TimeSpan.FromMilliseconds(16.7), -1);
        var next = ReplayFrameTimingPolicy.RealPtsMicroseconds(TimeSpan.FromMilliseconds(33.9), first);
        var clamped = ReplayFrameTimingPolicy.RealPtsMicroseconds(TimeSpan.FromMilliseconds(33.9), next);

        Assert.Equal(16_700, first);
        Assert.Equal(33_900, next);
        Assert.Equal(next + 1, clamped);
    }
}
