using System.Globalization;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipOverlayBurnLayoutTests
{
    private static ClipOverlayAsset Segment(double start) => new($"{start}.mp4", start, start + 2);

    [Fact]
    public void PlacementFollowsTheOutputFrameSoShareDownscalesWithIt()
    {
        var transform = new VideoOverlayTransform(.70, .05, .25);

        var export = ClipOverlayBurnLayout.Resolve(transform, VideoOverlayLayout.CameraAspectRatio, 1920, 1080);
        var share = ClipOverlayBurnLayout.Resolve(transform, VideoOverlayLayout.CameraAspectRatio, 1280, 720);

        Assert.Equal(480, export.Width);
        Assert.Equal(270, export.Height);
        Assert.Equal(1344, export.X);
        Assert.Equal(54, export.Y);
        // Two thirds the frame, so two thirds the overlay, in the same place.
        Assert.Equal(320, share.Width);
        Assert.Equal(180, share.Height);
        Assert.Equal(896, share.X);
        Assert.Equal(36, share.Y);
    }

    [Fact]
    public void TrimShiftsCoverageToOutputTimeAndSpeedDividesIt()
    {
        // Trim starts at 11s, so output time 0 is clip time 11s, and at 2x a
        // clip second is half an output second.
        var coverage = ClipOverlayBurnLayout.Coverage([new("0.mp4", 10, 12)], 11, 20, 2);

        Assert.Single(coverage);
        Assert.Equal(0, coverage[0].Start, 3);
        Assert.Equal(.5, coverage[0].End, 3);
    }

    [Fact]
    public void EnableIsFormattedInvariantlySoACommaLocaleCannotBreakTheEncode()
    {
        // "between(t,0,5,3,25)" is not a filter expression, it is a failed export.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var enable = ClipOverlayBurnLayout.Enable([(0.5, 3.25)], 10);
            Assert.Equal("between(t,0.5,3.25)", enable);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
