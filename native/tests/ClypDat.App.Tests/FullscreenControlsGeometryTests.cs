using Avalonia;
using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FullscreenControlsGeometryTests
{
    [Theory]
    [InlineData(0, 0, 3840, 2160, 1.5, 1245, 1962, 1350, 162)]
    [InlineData(-1920, 0, 1920, 1080, 1, -1410, 948, 900, 108)]
    [InlineData(-1080, -1920, 1080, 1920, 1.5, -1044, -198, 1008, 162)]
    [InlineData(0, 0, 2400, 1350, 1.25, 637, 1185, 1125, 135)]
    public void CentersControlsInPhysicalViewportWithDipInsets(int x, int y, int width, int height,
        double scale, int targetX, int targetY, int targetWidth, int targetHeight)
    {
        var viewport = new PixelRect(x, y, width, height);
        Assert.Equal(new PixelRect(targetX, targetY, targetWidth, targetHeight),
            FullscreenControlsGeometry.Place(viewport, scale, 108));
    }

    [Theory]
    [InlineData(1920, 1, 900, false)]
    [InlineData(1080, 1.5, 672, true)]
    [InlineData(808, 1, 760, false)]
    [InlineData(807, 1, 759, true)]
    public void NarrowDisplaysStackVolume(int pixelsWide, double scale, double expectedWidth, bool stack)
    {
        var width = FullscreenControlsGeometry.Width(new PixelRect(0, 0, pixelsWide, 1080), scale);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(stack, FullscreenControlsGeometry.StackVolume(width));
    }

    [Theory]
    [InlineData(30, 20, 2)]
    [InlineData(320, 240, 1.5)]
    [InlineData(1920, 1080, 0)]
    public void ControlsStayInsideShortViewports(int width, int height, double scale)
    {
        var viewport = new PixelRect(-width, -height, width, height);
        var controls = FullscreenControlsGeometry.Place(viewport, scale, 2000);
        Assert.True(controls.X >= viewport.X && controls.Y >= viewport.Y);
        Assert.True(controls.Right <= viewport.Right && controls.Bottom <= viewport.Bottom);
        Assert.True(controls.Width > 0 && controls.Height > 0);
    }
}
