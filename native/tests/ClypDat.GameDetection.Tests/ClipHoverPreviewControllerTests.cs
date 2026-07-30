using ClypDat.App.Services;

namespace ClypDat.GameDetection.Tests;

public sealed class ClipHoverPreviewControllerTests
{
    [Theory]
    [InlineData(24, 24)]
    [InlineData(29.97, 29.97)]
    [InlineData(59.94, 59.94)]
    [InlineData(60, 60)]
    [InlineData(120, 60)]
    [InlineData(240, 60)]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(double.NaN, 30)]
    [InlineData(double.PositiveInfinity, 30)]
    public void ResolveFrameRate_UsesRecordedRateWithinSafeBounds(double recorded, double expected)
    {
        Assert.Equal(expected, ClipHoverPreviewController.ResolveFrameRate(recorded));
    }

    [Fact]
    public void BuildDecoderArguments_PreservesFractionalFrameRate()
    {
        var arguments = ClipHoverPreviewController.BuildDecoderArguments("clip.mp4", (TimeSpan.Zero, TimeSpan.FromSeconds(5)), 29.97);

        Assert.Contains("fps=29.97,scale=480:270:force_original_aspect_ratio=decrease,pad=480:270:(ow-iw)/2:(oh-ih)/2", arguments);
    }
}
