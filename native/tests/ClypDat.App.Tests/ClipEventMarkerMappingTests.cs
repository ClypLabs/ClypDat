using ClypDat.App.Services;
using ClypDat.App.Controls;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipEventMarkerMappingTests
{
    [Theory]
    [InlineData("kill", "M12,3a9,9 0 1 0 0,18a9,9 0 1 0 0-18m0,4v10m-5-5h10")]
    [InlineData("headshot", "M12,3a9,9 0 1 0 0,18a9,9 0 1 0 0-18m0,4v10m-5-5h10")]
    [InlineData("not-a-known-event", "M12,3 21,12 12,21 3,12Z")]
    public void MapsEventFamiliesToCrispVectorGlyphs(string eventId, string glyph) => Assert.Equal(glyph, TimelineMarkerPresentation.AppearanceFor(eventId).Glyph);

    [Fact]
    public void GroupsDenseAndSimultaneousEventsWithoutDroppingOccurrences()
    {
        var markers = new[] { new ClipEventMarker("a", "A", 10), new ClipEventMarker("b", "B", 10), new ClipEventMarker("c", "C", 10.1), new ClipEventMarker("d", "D", 90) };
        var groups = TimelineMarkerPresentation.Group(markers, 100, TimeSpan.FromSeconds(100));
        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups[0].Markers.Count);
        Assert.Equal(4, groups.SelectMany(group => group.Markers).Count());
    }

    [Fact]
    public void FormatterKeepsHoursForLongMarkerTimestamps() => Assert.Equal("1:02:03", ClipDurationFormatter.Format(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3)));
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
