using ClypDat.App.Controls;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SeekRailControlTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 0)]
    [InlineData(50, 5)]
    [InlineData(92, 10)]
    [InlineData(100, 10)]
    public void FullscreenThumbMapsToBothEnds(double x, double expectedSeconds)
    {
        var position = SeekRailControl.PositionForPointer(TimeSpan.FromSeconds(10), x, 100, 16);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), position);
    }

    [Fact]
    public void HoverRailRetainsEdgeToEdgeMapping()
    {
        Assert.Equal(TimeSpan.FromSeconds(5),
            SeekRailControl.PositionForPointer(TimeSpan.FromSeconds(10), 50, 100, 0));
    }

    [Fact]
    public void ZeroDurationHasNoSeekPosition()
    {
        Assert.Equal(TimeSpan.Zero, SeekRailControl.PositionForPointer(TimeSpan.Zero, 50, 100, 16));
    }
}
