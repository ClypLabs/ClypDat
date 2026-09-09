using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SpotifyOverlayManipulationTests
{
    [Fact]
    public void MovingUsesOutputCoordinatesAndStaysInsideVideo()
    {
        var start = new SpotifyOverlayTransform(.2, .3, .25);
        var moved = SpotifyOverlayManipulation.Apply(start, SpotifyOverlayDragMode.Move, 96, 54, 1920, 1080);
        Assert.Equal(.25, moved.X, 8);
        Assert.Equal(.35, moved.Y, 8);
        Assert.Equal(start.Width, moved.Width);
        Assert.Equal(moved, SpotifyOverlayManipulation.Apply(start, SpotifyOverlayDragMode.Move, 32, 18, 640, 360));
        var edge = SpotifyOverlayManipulation.Apply(start, SpotifyOverlayDragMode.Move, 10000, 10000, 1920, 1080);
        var bounds = SpotifyOverlayLayout.Resolve(1920, 1080, null, edge);
        Assert.Equal(1920, bounds.X + bounds.Width);
        Assert.Equal(1080, bounds.Y + bounds.Height);
    }

    [Theory]
    [InlineData("TopLeft", -1, -1)]
    [InlineData("TopRight", 1, -1)]
    [InlineData("BottomLeft", -1, 1)]
    [InlineData("BottomRight", 1, 1)]
    public void ResizingKeepsOppositeCornerAndAspect(string handle, int xDirection, int yDirection)
    {
        var mode = Enum.Parse<SpotifyOverlayDragMode>(handle);
        var start = new SpotifyOverlayTransform(.2, .3, .25);
        var resized = SpotifyOverlayManipulation.Apply(start, mode, 120 * xDirection,
            120 / SpotifyOverlayLayout.AspectRatio * yDirection, 1920, 1080);
        Assert.Equal(600.0 / 1920, resized.Width, 8);
        var beforeAnchorX = start.X + (xDirection < 0 ? start.Width : 0);
        var afterAnchorX = resized.X + (xDirection < 0 ? resized.Width : 0);
        var beforeAnchorY = start.Y + (yDirection < 0 ? start.Width * 1920 / SpotifyOverlayLayout.AspectRatio / 1080 : 0);
        var afterAnchorY = resized.Y + (yDirection < 0 ? resized.Width * 1920 / SpotifyOverlayLayout.AspectRatio / 1080 : 0);
        Assert.Equal(beforeAnchorX, afterAnchorX, 8);
        Assert.Equal(beforeAnchorY, afterAnchorY, 8);
        var tiny = SpotifyOverlayManipulation.Apply(start, mode, -10000 * xDirection, -10000 * yDirection, 1920, 1080);
        Assert.Equal(SpotifyOverlayLayout.MinimumWidth, tiny.Width, 8);
        var huge = SpotifyOverlayManipulation.Apply(start, mode, 10000 * xDirection, 10000 * yDirection, 1920, 1080);
        var bounds = SpotifyOverlayLayout.Resolve(1920, 1080, null, huge);
        Assert.InRange(bounds.X, 0, 1920 - bounds.Width);
        Assert.InRange(bounds.Y, 0, 1080 - bounds.Height);
    }

    [Fact]
    public void RotationTurnsAroundCardCenterAndSnapsNearFifteenDegrees()
    {
        var start = new SpotifyOverlayTransform(.2, .3, .25);
        var center = new Avalonia.Point(624, 406.75);
        var rotated = SpotifyOverlayManipulation.Rotate(start, new Avalonia.Point(center.X + 100, center.Y),
            new Avalonia.Point(center.X, center.Y + 100), 1920, 1080);
        Assert.Equal(90, rotated.RotationDegrees, 8);
        Assert.Equal(start.X, rotated.X, 8);
        Assert.Equal(start.Y, rotated.Y, 8);
    }

    [Fact]
    public void RotatedResizeKeepsRotationAndOppositeCorner()
    {
        var start = new SpotifyOverlayTransform(.2, .3, .25, 30);
        var resized = SpotifyOverlayManipulation.Apply(start, SpotifyOverlayDragMode.BottomRight, 120, 70, 1920, 1080);
        Assert.Equal(30, resized.RotationDegrees, 8);
        Assert.True(resized.Width > start.Width);
    }
}
