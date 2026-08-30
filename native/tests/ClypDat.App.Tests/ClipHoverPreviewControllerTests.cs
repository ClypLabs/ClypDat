using Avalonia;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipHoverPreviewControllerTests
{
    [Theory]
    [InlineData(1.0, 320, 180)]
    [InlineData(1.25, 416, 234)]
    [InlineData(1.5, 480, 270)]
    public void ResolvePreviewSize_DisplayScaling_UsesExactEvenSixteenByNineCanvas(
        double renderScaling, int expectedWidth, int expectedHeight)
    {
        var size = ClipHoverPreviewController.ResolvePreviewSize(new Size(320, 180), renderScaling);

        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), size);
        Assert.True(size.Width <= ClipHoverPreviewController.MaximumPreviewWidth);
        Assert.Equal(0, size.Width % 2);
        Assert.Equal(0, size.Height % 2);
        Assert.Equal(size.Width * 9, size.Height * 16);
    }

    [Fact]
    public void ResolvePreviewSize_FractionalTileHeight_UsesWidthDerivedSixteenByNineCanvas()
    {
        var size = ClipHoverPreviewController.ResolvePreviewSize(new Size(220, 123.75), 1.25);

        Assert.Equal(new PixelSize(288, 162), size);
        Assert.Equal(size.Width * 9, size.Height * 16);
    }

    [Fact]
    public void ResolvePreviewSize_LargeTile_CapsExactCanvasAt640By360()
    {
        var size = ClipHoverPreviewController.ResolvePreviewSize(new Size(1000, 562.5), 1.5);

        Assert.Equal(new PixelSize(640, 360), size);
    }

    [Fact]
    public void BuildDecoderArguments_NormalClip_UsesCoverThenCenterCrop()
    {
        var arguments = ClipHoverPreviewController.BuildDecoderArguments(
            "clip.mp4", (TimeSpan.Zero, TimeSpan.FromSeconds(3)), 60, new PixelSize(320, 180));

        Assert.Equal(
            "fps=60,scale=w=320:h=180:flags=bilinear:force_original_aspect_ratio=increase,crop=320:180:(in_w-out_w)/2:(in_h-out_h)/2",
            Filter(arguments));
    }

    [Fact]
    public void BuildDecoderArguments_EditedCrop_UsesCropThenContainAndCenterPad()
    {
        var arguments = ClipHoverPreviewController.BuildDecoderArguments(
            "clip.mp4", (TimeSpan.Zero, TimeSpan.FromSeconds(3)), 60, new PixelSize(320, 180), "crop=900:900:10:20");

        Assert.Equal(
            "fps=60,crop=900:900:10:20,scale=w=320:h=180:flags=bilinear:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2",
            Filter(arguments));
    }

    private static string Filter(IReadOnlyList<string> arguments) => arguments[Array.IndexOf(arguments.ToArray(), "-vf") + 1];
}
