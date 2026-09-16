using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClypDat.App.Services;
using ClypDat.App.Controls;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipEventMarkerMappingTests
{
    [Theory]
    [InlineData("kill", null)]
    [InlineData("triple_kill_streak", null)]
    [InlineData("win", null)]
    [InlineData("team_wipe", null)]
    [InlineData("kill", "kill")]
    [InlineData("kill", "win")]
    [Trait("Category", "IsolatedSTA")]
    public void HaloAndDiscShareTheSameCentre(string eventId, string? secondEventId)
    {
        AvaloniaTestThread.Run(() =>
        {
            var markers = new List<ClipEventMarker> { new(eventId, eventId, 50) };
            if (secondEventId is not null) markers.Add(new(secondEventId, secondEventId, 50));
            var control = new TimelineMarkersControl
            {
                Width = 400,
                Height = 60,
                Duration = TimeSpan.FromSeconds(100),
                VideoTrackHeight = 60,
                Markers = markers
            };
            control.Measure(new Size(400, 60));
            control.Arrange(new Rect(0, 0, 400, 60));

            var button = Assert.IsType<Button>(Assert.Single(control.Children));
            var content = Assert.IsType<Panel>(button.Content);
            content.Measure(new Size(button.Width, button.Height));
            content.Arrange(new Rect(0, 0, button.Width, button.Height));
            var halo = Assert.IsType<Border>(content.Children[0]);
            var disc = Assert.IsType<Border>(content.Children[2]);

            Assert.True(halo.Bounds.Width > disc.Bounds.Width);
            Assert.Equal(disc.Bounds.Center.X, halo.Bounds.Center.X, 3);
            Assert.Equal(disc.Bounds.Center.Y, halo.Bounds.Center.Y, 3);
        }, TimeSpan.FromSeconds(10), "Marker layout timed out.");
    }

    [Theory]
    [InlineData("kill", "M12,3a9,9 0 1 0 0,18a9,9 0 1 0 0-18m0,4v10m-5-5h10")]
    [InlineData("headshot", "M12,3a9,9 0 1 0 0,18a9,9 0 1 0 0-18m0,4v10m-5-5h10")]
    [InlineData("not-a-known-event", "M12,3 21,12 12,21 3,12Z")]
    public void MapsEventFamiliesToCrispVectorGlyphs(string eventId, string glyph) => Assert.Equal(glyph, TimelineMarkerPresentation.AppearanceFor(eventId).Glyph);

    // The marker draws its glyph with a pen or a brush depending on this flag,
    // and getting it wrong is silent: a crosshair filled instead of stroked is
    // a solid disc, and a plus sign filled is nothing at all, because neither
    // encloses any area.
    [Theory]
    [InlineData("kill", false)]
    [InlineData("headshot", false)]
    [InlineData("assist", false)]
    [InlineData("death", false)]
    [InlineData("objective_capture", true)]
    [InlineData("win", true)]
    [InlineData("not-a-known-event", true)]
    public void LineGlyphsAreStrokedAndSilhouettesAreFilled(string eventId, bool filled) =>
        Assert.Equal(filled, TimelineMarkerPresentation.AppearanceFor(eventId).Filled);

    // Every glyph is drawn on a 24x24 grid but none of them fills it: the X
    // occupies x 7-17, the flame 5-19 and taller than wide. Scaling the box
    // they sit in leaves each one a different distance from the middle, which
    // is how the flame ended up low and to the right of its disc.
    [Theory]
    [InlineData("kill")]
    [InlineData("death")]
    [InlineData("triple_kill_streak")]
    [InlineData("team_wipe")]
    [InlineData("win")]
    [InlineData("not-a-known-event")]
    public void EveryGlyphCentresOnTheSamePointWhateverGridItWasDrawnOn(string eventId)
    {
        const double size = 11;
        var glyph = Geometry.Parse(TimelineMarkerPresentation.AppearanceFor(eventId).Glyph);
        var centred = glyph.Bounds.TransformToAABB(TimelineMarkersControl.CentreGlyph(glyph.Bounds, size));

        Assert.Equal(size / 2, centred.Center.X, 3);
        Assert.Equal(size / 2, centred.Center.Y, 3);
        Assert.Equal(size, Math.Max(centred.Width, centred.Height), 3);
    }

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
