using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AutoClipWindowPolicyTests
{
    [Theory]
    [InlineData(10, 60, 30)]
    [InlineData(80, 120, 96)]
    [InlineData(80, 60, 60)]
    [InlineData(80, 15, 15)]
    public void HelldiversWindowCoversWholeStreakWithinHistory(int streakLength, int history, int expectedLength)
    {
        var events = new[] { new AutoClipEvent("killstreak-50", "Killstreak ×57", Event) };
        var request = new AutoClipRequest("helldivers2", "Helldivers 2", "killstreak-50", "Killstreak ×57",
            "Killstreak ×57", Event.AddSeconds(-streakLength - 10), Event.AddSeconds(6), Events: events);
        var window = AutoClipWindowPolicy.ForRequest(request, TimeSpan.FromSeconds(history));
        Assert.Equal(expectedLength, (window.EndUtc - window.StartUtc).TotalSeconds);
        Assert.Equal(Event.AddSeconds(6), window.EndUtc);
        var marker = Assert.Single(ClipEventMarkerMapping.FromEvents(events, window.StartUtc, window.EndUtc));
        Assert.Equal(expectedLength - 6, marker.OffsetSeconds);
    }

    [Fact]
    public void SaveQueueDelayClampsHistoryBeforeMappingMarkers()
    {
        var window = AutoClipWindowPolicy.ClampToHistory(Event.AddSeconds(-90), Event.AddSeconds(6),
            TimeSpan.FromSeconds(60), Event.AddSeconds(10));
        Assert.Equal(Event.AddSeconds(-50), window.StartUtc);
        Assert.Equal(Event.AddSeconds(6), window.EndUtc);
        var marker = Assert.Single(ClipEventMarkerMapping.FromEvents(
            new[] { new AutoClipEvent("killstreak-50", "Killstreak ×57", Event) }, window.StartUtc, window.EndUtc));
        Assert.Equal(50, marker.OffsetSeconds);
    }

    [Fact]
    public void FreshReplayBufferMapsMarkersAgainstActualSavedHistory()
    {
        var events = new[] { new AutoClipEvent("killstreak-50", "Killstreak ×57", Event) };
        var source = new SpotifySourceWindow(MonotonicClock.ToSharedSeconds(Event.AddSeconds(-8)), 14, MonotonicClock.BootId);
        var marker = Assert.Single(ClipEventMarkerMapping.FromSavedWindow(events,
            Event.AddSeconds(-90), Event.AddSeconds(6), source));
        Assert.Equal(8, marker.OffsetSeconds, precision: 3);
    }

    private static readonly DateTime Event = new(2026, 9, 6, 0, 8, 32, DateTimeKind.Utc);
    private static readonly TimeSpan OneMinuteBuffer = TimeSpan.FromMinutes(1);

    // An Overwatch double kill is an 8s lead and a 6s tail - fourteen seconds,
    // which is the event and nothing else. The clip should still open half a
    // minute earlier.
    [Fact]
    public void AShortEventWindowIsBackedOffToTheMinimum()
    {
        var (startUtc, endUtc) = AutoClipWindowPolicy.Extend(
            Event.AddSeconds(-8), Event.AddSeconds(6), OneMinuteBuffer);

        Assert.Equal(Event.AddSeconds(6), endUtc);
        Assert.Equal(TimeSpan.FromSeconds(30), endUtc - startUtc);
    }

    // A streak that escalated for most of a minute must keep its opening kill,
    // not get trimmed back to thirty seconds.
    [Fact]
    public void AWindowLongerThanTheMinimumIsLeftAlone()
    {
        var start = Event.AddSeconds(-40);
        var end = Event.AddSeconds(6);

        var extended = AutoClipWindowPolicy.Extend(start, end, OneMinuteBuffer);

        Assert.Equal(start, extended.StartUtc);
        Assert.Equal(end, extended.EndUtc);
    }

    // Asking for more history than the buffer holds would only return whatever is
    // actually there, so the window says what it can honestly deliver.
    [Fact]
    public void TheWindowNeverAsksForMoreHistoryThanTheBufferHolds()
    {
        var (startUtc, endUtc) = AutoClipWindowPolicy.Extend(
            Event.AddSeconds(-8), Event.AddSeconds(6), TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(15), endUtc - startUtc);
    }

    // The tail is the anchor: the action sits at the end of the clip with the
    // run-up in front of it, never re-centred.
    [Fact]
    public void TheEndIsNeverMoved()
    {
        foreach (var available in new[] { TimeSpan.FromSeconds(10), OneMinuteBuffer, TimeSpan.FromMinutes(5) })
        {
            var (_, endUtc) = AutoClipWindowPolicy.Extend(Event.AddSeconds(-8), Event.AddSeconds(6), available);
            Assert.Equal(Event.AddSeconds(6), endUtc);
        }
    }
}
