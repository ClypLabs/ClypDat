using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplayFrameRatePolicyTests
{
    [Fact]
    public void SelectableRates_Exclude144()
    {
        Assert.Equal(new[] { 30, 60, 90, 120 }, ReplayFrameRatePolicy.Selectable);
        Assert.DoesNotContain(144, ReplayFrameRatePolicy.Selectable);
    }

    [Theory]
    [InlineData(144, 120)]
    [InlineData(120, 120)]
    [InlineData(30, 30)]
    [InlineData(121, 120)]
    public void NormalizePersisted_ClampsLegacy144(int value, int expected) =>
        Assert.Equal(expected, ReplayFrameRatePolicy.NormalizePersisted(value));
}
