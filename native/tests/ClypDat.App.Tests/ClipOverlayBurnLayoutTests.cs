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
    public void DimensionsAreEvenAndTheOverlayStaysInsideTheFrame()
    {
        // Odd scale targets produce chroma artifacts against a yuv420p track.
        var odd = ClipOverlayBurnLayout.Resolve(new(0, 0, .333), 16 / 9d, 1919, 1079);
        Assert.Equal(0, odd.Width % 2);
        Assert.Equal(0, odd.Height % 2);

        // Pushed past the right edge, it clamps rather than emitting an overlay
        // x that would put it off-frame.
        var pushed = ClipOverlayBurnLayout.Resolve(new(.95, .95, .25), 16 / 9d, 1920, 1080);
        Assert.True(pushed.X + pushed.Width <= 1920);
        Assert.True(pushed.Y + pushed.Height <= 1080);
    }

    [Fact]
    public void AWideKeyboardAndANearlySquareOneBothSurvive()
    {
        var full = ClipOverlayBurnLayout.Resolve(new(.05, .70, .35), KeyboardOverlayCatalog.Get("QWERTY Full").AspectRatio, 1920, 1080);
        var arrows = ClipOverlayBurnLayout.Resolve(new(.05, .70, .35), KeyboardOverlayCatalog.Get("Arrows").AspectRatio, 1920, 1080);

        Assert.Equal(672, full.Width);
        Assert.Equal(182, full.Height);   // 1989:540 is very wide
        Assert.Equal(672, arrows.Width);
        Assert.True(arrows.Height > 400); // 679:434 is nearly square
    }

    [Fact]
    public void ContiguousSegmentsMergeIntoOneSpanAndAGapSplitsThem()
    {
        var contiguous = Enumerable.Range(0, 15).Select(i => Segment(i * 2)).ToArray();

        var merged = ClipOverlayBurnLayout.Coverage(contiguous, 0, 30, 1);

        Assert.Single(merged);
        Assert.Equal(0, merged[0].Start, 3);
        Assert.Equal(30, merged[0].End, 3);

        // Drop the segment covering 14s-16s.
        var holed = contiguous.Where(asset => asset.StartSeconds != 14).ToArray();
        var split = ClipOverlayBurnLayout.Coverage(holed, 0, 30, 1);

        Assert.Equal(2, split.Count);
        Assert.Equal(14, split[0].End, 3);
        Assert.Equal(16, split[1].Start, 3);
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
    public void SegmentsOutsideTheTrimContributeNothing()
    {
        Assert.Empty(ClipOverlayBurnLayout.Coverage([Segment(0)], 10, 20, 1));
        Assert.Empty(ClipOverlayBurnLayout.Coverage([Segment(30)], 10, 20, 1));
        Assert.Empty(ClipOverlayBurnLayout.Coverage(null, 0, 10, 1));
        Assert.Empty(ClipOverlayBurnLayout.Coverage([], 0, 10, 1));
    }

    [Fact]
    public void FullCoverageNeedsNoEnableExpressionAndAGapProducesOne()
    {
        var full = ClipOverlayBurnLayout.Coverage(Enumerable.Range(0, 5).Select(i => Segment(i * 2)).ToArray(), 0, 10, 1);
        Assert.Null(ClipOverlayBurnLayout.Enable(full, 10));

        var holed = ClipOverlayBurnLayout.Coverage([Segment(0), Segment(2), Segment(6)], 0, 10, 1);
        Assert.Equal("between(t,0,4)+between(t,6,8)", ClipOverlayBurnLayout.Enable(holed, 10));

        Assert.Null(ClipOverlayBurnLayout.Enable([], 10));
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
