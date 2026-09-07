using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipEventMarkerMappingTests
{
    [Fact]
    public void MapsEveryEventToClipRelativeSeconds()
    {
        var start = DateTime.UtcNow;
        var markers = ClipEventMarkerMapping.FromEvents(new[]
        {
            new AutoClipEvent("kill", "Kill", start.AddSeconds(4)),
            new AutoClipEvent("triple", "Triple Kill", start.AddSeconds(8))
        }, start, start.AddSeconds(12));

        Assert.Equal(new[] { 4d, 8d }, markers.Select(marker => marker.OffsetSeconds).ToArray());
    }

    [Fact]
    public void DropsEventsOutsideSavedWindow()
    {
        var start = DateTime.UtcNow;
        var markers = ClipEventMarkerMapping.FromEvents(new[]
        {
            new AutoClipEvent("before", "Before", start.AddSeconds(-1)),
            new AutoClipEvent("inside", "Inside", start.AddSeconds(3)),
            new AutoClipEvent("after", "After", start.AddSeconds(13))
        }, start, start.AddSeconds(12));

        var marker = Assert.Single(markers);
        Assert.Equal("inside", marker.EventId);
        Assert.Equal(3, marker.OffsetSeconds);
    }
}
