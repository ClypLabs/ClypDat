using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplayFramePacerTests
{
    [Theory]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(90, 90)]
    [InlineData(120, 120)]
    [InlineData(144, 120)]
    public void ConstantRateKeepsSelectedCadenceAcrossSourceGap(int requestedFrameRate, int expectedFrameRate)
    {
        var pacer = new ReplayFramePacer(requestedFrameRate, variableFrameRate: false);

        for (var index = 0; index < expectedFrameRate * 2; index++)
        {
            var actual = pacer.Next(TimeSpan.FromSeconds(index / (double)expectedFrameRate), sourceAdvanced: index == 0);
            var expected = (long)Math.Round(index * 1_000_000.0 / expectedFrameRate);
            Assert.InRange(Math.Abs(actual - expected), 0, 1);
        }
    }

    [Fact]
    public void VariableRatePreservesFreshTimingAndPadsOnlyGapTicks()
    {
        var pacer = new ReplayFramePacer(60, variableFrameRate: true);

        Assert.Equal(0, pacer.Next(TimeSpan.Zero, sourceAdvanced: true));
        Assert.Equal(16_667, pacer.Next(TimeSpan.FromMilliseconds(16), sourceAdvanced: false));
        Assert.Equal(50_000, pacer.Next(TimeSpan.FromMilliseconds(50), sourceAdvanced: true));
        Assert.Equal(66_667, pacer.Next(TimeSpan.FromMilliseconds(66), sourceAdvanced: false));
    }

    [Fact]
    public void RateChangeRemainsStrictlyMonotonic()
    {
        var pacer = new ReplayFramePacer(60, variableFrameRate: false);
        var first = pacer.Next(TimeSpan.Zero, sourceAdvanced: true);
        pacer.SetFrameRate(144);
        var second = pacer.Next(TimeSpan.FromMilliseconds(7), sourceAdvanced: false);

        Assert.True(second > first);
    }
}
