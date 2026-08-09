using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplaySegmentWindowSelectorTests
{
    [Fact]
    public void Select_UsesRequestedUtcWindowAndFrontOffset()
    {
        var origin = DateTime.UtcNow.AddMinutes(-1);
        var segments = new[]
        {
            Segment("one", origin, origin.AddSeconds(20), 20),
            Segment("two", origin.AddSeconds(20), origin.AddSeconds(35), 15)
        };

        var result = ReplaySegmentWindowSelector.Select(segments, origin.AddSeconds(10), origin.AddSeconds(30));

        Assert.Equal(new[] { "one", "two" }, result.Segments.Select(segment => segment.Path));
        Assert.Equal(10, result.FirstOffsetSeconds, 6);
        Assert.Equal(20, result.DurationSeconds, 6);
    }

    [Fact]
    public void Select_DropsSuffixBeforeMaterialCaptureGap()
    {
        var origin = DateTime.UtcNow.AddMinutes(-1);
        var segments = new[]
        {
            Segment("old", origin, origin.AddSeconds(20), 20),
            Segment("new", origin.AddSeconds(25), origin.AddSeconds(35), 10)
        };

        var result = ReplaySegmentWindowSelector.Select(segments, origin, origin.AddSeconds(35));

        Assert.Equal(new[] { "new" }, result.Segments.Select(segment => segment.Path));
        Assert.Equal(10, result.DurationSeconds, 6);
    }

    [Fact]
    public void Select_ReturnsAvailableFreshCaptureWhenWindowStartsBeforeRecording()
    {
        var origin = DateTime.UtcNow.AddMinutes(-1);
        var segments = new[] { Segment("fresh", origin.AddSeconds(10), origin.AddSeconds(22), 12) };

        var result = ReplaySegmentWindowSelector.Select(segments, origin, origin.AddSeconds(22));

        Assert.Equal(12, result.DurationSeconds, 6);
        Assert.Equal(0, result.FirstOffsetSeconds, 6);
    }

    private static ReplaySegmentWindow Segment(string path, DateTime start, DateTime end, double duration) =>
        new(path, start, end, TimeSpan.FromSeconds(duration));
}
