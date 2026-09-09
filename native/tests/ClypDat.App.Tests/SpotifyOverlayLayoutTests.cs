using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SpotifyOverlayLayoutTests
{
    [Theory]
    [InlineData("Top Left", 0, 14)]
    [InlineData("Top Right", 1514, 14)]
    [InlineData("Center Left", 0, 470)]
    [InlineData("Center Right", 1514, 470)]
    [InlineData("Bottom Left", 0, 926)]
    [InlineData("Bottom Right", 1514, 926)]
    public void ExistingPlacementsRetainTheirSizeAndEdges(string position, int x, int y)
    {
        Assert.Equal(new SpotifyOverlayBounds(x, y, 406, 140), SpotifyOverlayLayout.Resolve(1920, 1080, position));
    }

    [Theory]
    [InlineData(1920, 1080, 192, 216, 576, 199)]
    [InlineData(1280, 720, 128, 144, 384, 133)]
    [InlineData(640, 360, 64, 72, 192, 67)]
    public void EditedGeometryScalesWithOutput(int frameWidth, int frameHeight, int x, int y, int width, int height)
    {
        var bounds = SpotifyOverlayLayout.Resolve(frameWidth, frameHeight, "Bottom Right", new(.1, .2, .3));
        Assert.Equal(new SpotifyOverlayBounds(x, y, width, height), bounds);
        Assert.InRange(Math.Abs(bounds.Width / SpotifyOverlayLayout.AspectRatio - bounds.Height), 0, 1);
    }

    [Theory]
    [InlineData(1000, 100)]
    [InlineData(100, 1000)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    public void OversizedAndOutOfFrameEditsFitInsideOutput(int frameWidth, int frameHeight)
    {
        var bounds = SpotifyOverlayLayout.Resolve(frameWidth, frameHeight, "Top Left", new(20, -10, 10));
        Assert.InRange(bounds.X, 0, Math.Max(1, frameWidth) - bounds.Width);
        Assert.Equal(0, bounds.Y);
        Assert.InRange(bounds.Width, 1, Math.Max(1, frameWidth));
        Assert.InRange(bounds.Height, 1, Math.Max(1, frameHeight));
    }

    [Fact]
    public void InvalidStoredNumbersAndTinyResizeRemainUsable()
    {
        var invalid = SpotifyOverlayLayout.Normalize(1920, 1080, new(double.NaN, double.PositiveInfinity, double.NegativeInfinity));
        Assert.Equal(0, invalid.X);
        Assert.Equal(0, invalid.Y);
        Assert.InRange(invalid.Width, .2, .22);
        var tiny = SpotifyOverlayLayout.Resolve(1920, 1080, "Top Left", new(1, 1, 0));
        Assert.Equal(96, tiny.Width);
        Assert.Equal(1920, tiny.X + tiny.Width);
        Assert.Equal(1080, tiny.Y + tiny.Height);
    }

    [Theory]
    [InlineData(518, 331)]
    [InlineData(1366, 345)]
    [InlineData(1920, 496)]
    public void MovingDoesNotGrowWidthThroughNormalizedRounding(int frameWidth, int cardWidth)
    {
        var transform = new SpotifyOverlayTransform(.1, .1, cardWidth / (double)frameWidth);
        for (var move = 0; move < 10; move++)
        {
            var bounds = SpotifyOverlayLayout.Resolve(frameWidth, 1080, "Top Left", transform);
            Assert.Equal(cardWidth, bounds.Width);
            transform = transform with { X = .2, Width = bounds.Width / (double)frameWidth };
        }
    }

    [Fact]
    public void ResolvedCoordinatesFollowCropSpeedAndScaleInFilterGraph()
    {
        var effects = ClipRenderFilters.BuildVideoFilter(new(160, 20, 320, 320), 2, "scale=640:360");
        var bounds = SpotifyOverlayLayout.Resolve(640, 360, "Bottom Right", new(.125, .5, .5));
        var graph = ClipRenderFilters.ComposeWithAnimation(effects, "Bottom Right", "[0:v]", "[video]", bounds);
        Assert.Equal("[0:v]crop=320:320:160:20,setpts=PTS/2,scale=640:360[spotifybase];[spotifybase][1:v:0]overlay=80:180:eof_action=pass:repeatlast=0:alpha=straight[video]", graph);
    }

    [Fact]
    public void ZoomProjectsTheFullFrameBeforeClippingTheCard()
    {
        var transform = new SpotifyOverlayTransform(.1, .2, .3);
        var fullFrame = new SpotifyOverlayBounds(-480, -270, 1920, 1080);
        var viewport = new SpotifyOverlayBounds(0, 0, 960, 540);
        var projection = SpotifyOverlayLayout.Project(fullFrame, viewport, "Top Left", transform);
        Assert.Equal(new SpotifyOverlayBounds(-288, -54, 576, 199), projection.CardBounds);
        Assert.Equal(new SpotifyOverlayBounds(0, 0, 288, 145), projection.VisibleBounds);

        var moved = SpotifyOverlayManipulation.Apply(transform, SpotifyOverlayDragMode.Move, 96, 54, fullFrame.Width, fullFrame.Height);
        var afterMove = SpotifyOverlayLayout.Project(fullFrame, viewport, "Top Left", moved);
        Assert.Equal(projection.CardBounds.Width, afterMove.CardBounds.Width);
        Assert.Equal(projection.CardBounds.Height, afterMove.CardBounds.Height);
        Assert.Equal(projection.CardBounds.X + 96, afterMove.CardBounds.X);
        Assert.Equal(projection.CardBounds.Y + 54, afterMove.CardBounds.Y);
    }

    [Fact]
    public void PanChangesOnlyProjectedCoordinates()
    {
        var transform = new SpotifyOverlayTransform(.1, .2, .3);
        var viewport = new SpotifyOverlayBounds(0, 0, 960, 540);
        var projection = SpotifyOverlayLayout.Project(new(-480, -170, 1920, 1080), viewport, "Top Left", transform);
        Assert.Equal(new SpotifyOverlayBounds(-288, 46, 576, 199), projection.CardBounds);
        Assert.Equal(new SpotifyOverlayBounds(0, 46, 288, 199), projection.VisibleBounds);
    }

    [Fact]
    public void CroppedOutputKeepsItsAspectWhenViewportClipsItsBottom()
    {
        var projection = SpotifyOverlayLayout.Project(new(100, 50, 540, 960), new(0, 0, 640, 540),
            "Bottom Right", new(.1, .4, .6));
        Assert.Equal(new SpotifyOverlayBounds(154, 434, 324, 112), projection.CardBounds);
        Assert.Equal(new SpotifyOverlayBounds(154, 434, 324, 106), projection.VisibleBounds);
    }

    [Fact]
    public void OffscreenCardRetainsItsGeometryButHasNoVisiblePixels()
    {
        var fullFrame = new SpotifyOverlayBounds(-480, -270, 1920, 1080);
        var projection = SpotifyOverlayLayout.Project(fullFrame, new(0, 0, 960, 540), "Bottom Right");
        Assert.Equal(new SpotifyOverlayBounds(1034, 656, 406, 140), projection.CardBounds);
        Assert.Equal(0, projection.VisibleBounds.Width);
        Assert.Equal(0, projection.VisibleBounds.Height);
    }

    [Fact]
    public void RotationUsesExpandedTransparentRasterAndKeepsLegacyGeometryAtZero()
    {
        var plain = SpotifyOverlayLayout.ResolveRenderBounds(1920, 1080, "Bottom Left", new(.1, .2, .3));
        var rotated = SpotifyOverlayLayout.ResolveRenderBounds(1920, 1080, "Bottom Left", new(.1, .2, .3, 45));
        Assert.Equal(SpotifyOverlayLayout.Resolve(1920, 1080, "Bottom Left", new(.1, .2, .3)), plain);
        Assert.True(rotated.Width * rotated.Height > plain.Width * plain.Height);
        Assert.True(rotated.Height > plain.Height);
    }

    [Fact]
    public void RotationNormalizesIntoStableHalfTurnRange()
    {
        Assert.Equal(15, SpotifyOverlayLayout.Normalize(1920, 1080, new(.1, .2, .3, 375)).RotationDegrees);
        Assert.Equal(0, SpotifyOverlayLayout.Normalize(1920, 1080, new(.1, .2, .3, 720)).RotationDegrees);
    }

    [Fact]
    public void TransformSideFollowsCenterAndMidpointUsesLeftLayout()
    {
        Assert.False(SpotifyOverlayLayout.IsRight(1920, 1080, "Bottom Right", new(.25, .2, .5)));
        Assert.True(SpotifyOverlayLayout.IsRight(1920, 1080, "Bottom Left", new(.26, .2, .5)));
    }
}
