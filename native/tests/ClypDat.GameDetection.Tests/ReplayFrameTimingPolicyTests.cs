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

    [Fact]
    public void VariableDeadlineStaysOnTheSelectedTimelineDespiteTimerJitter()
    {
        var interval = TimeSpan.FromSeconds(1.0 / 60);
        var deadline = TimeSpan.Zero;

        Assert.True(ReplayFrameTimingPolicy.TryAdvanceVariableDeadline(TimeSpan.FromMilliseconds(16.2), interval, ref deadline));
        Assert.Equal(interval, deadline);

        // This is slightly before the nominal 33.333ms deadline. The tolerance
        // prevents a normal Windows timer wake-up from skipping the whole slot.
        Assert.True(ReplayFrameTimingPolicy.TryAdvanceVariableDeadline(TimeSpan.FromMilliseconds(32.9), interval, ref deadline));
        Assert.Equal(TimeSpan.FromTicks(interval.Ticks * 2), deadline);
    }

    [Fact]
    public void VariableDeadlineCoalescesLongGapsWithoutBurstingDuplicateFrames()
    {
        var interval = TimeSpan.FromSeconds(1.0 / 60);
        var deadline = TimeSpan.Zero;

        Assert.True(ReplayFrameTimingPolicy.TryAdvanceVariableDeadline(TimeSpan.FromMilliseconds(100), interval, ref deadline));
        Assert.Equal(TimeSpan.FromTicks(interval.Ticks * 6), deadline);
    }
}
