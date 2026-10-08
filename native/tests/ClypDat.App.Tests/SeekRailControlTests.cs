using ClypDat.App.Controls;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SeekRailControlTests
{
    [Theory]
    [InlineData(-100, 0)]
    [InlineData(0, 0)]
    [InlineData(8, 0)]
    [InlineData(50, 5)]
    [InlineData(92, 10)]
    [InlineData(100, 10)]
    [InlineData(200, 10)]
    public void PlaybackThumbMapsToBothEnds(double x, double expectedSeconds)
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

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(8, 0.8)]
    [InlineData(25, 2.5)]
    [InlineData(50, 5)]
    [InlineData(92, 9.2)]
    [InlineData(100, 10)]
    [InlineData(110, 10)]
    public void EdgeToEdgeHoverThumbUsesFullVideoWidth(double x, double expectedSeconds)
    {
        Assert.Equal((0d, 100d), SeekRailControl.RailBounds(100, 16, insetForThumb: false));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            SeekRailControl.PositionForPointer(TimeSpan.FromSeconds(10), x, 100, 16, insetForThumb: false));
    }

    [Theory]
    [InlineData(100, 16, 8, 84)]
    [InlineData(16, 16, 8, 0)]
    [InlineData(10, 16, 5, 0)]
    [InlineData(0, 16, 0, 0)]
    [InlineData(100, 0, 0, 100)]
    public void ThumbFitsInsideRailBounds(double width, double diameter, double left, double railWidth)
    {
        Assert.Equal((left, railWidth), SeekRailControl.RailBounds(width, diameter));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(16)]
    public void CollapsedRailCannotSeek(double width)
    {
        Assert.Equal(TimeSpan.Zero, SeekRailControl.PositionForPointer(TimeSpan.FromSeconds(10), 200, width, 16));
    }
}
