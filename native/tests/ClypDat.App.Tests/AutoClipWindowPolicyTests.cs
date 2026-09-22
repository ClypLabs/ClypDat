using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AutoClipWindowPolicyTests
{
    [Fact]
    public void SaveQueueDelayClampsHistoryBeforeMappingMarkers()
    {
        var window = AutoClipWindowPolicy.ClampToHistory(Event.AddSeconds(-90), Event.AddSeconds(6),
            TimeSpan.FromSeconds(60), Event.AddSeconds(10));
        Assert.Equal(Event.AddSeconds(-50), window.StartUtc);
        Assert.Equal(Event.AddSeconds(6), window.EndUtc);
        var marker = Assert.Single(ClipEventMarkerMapping.FromEvents(
            new[] { new AutoClipEvent("killstreak", "Killstreak ×57", Event) }, window.StartUtc, window.EndUtc));
        Assert.Equal(50, marker.OffsetSeconds);
    }

    [Fact]
    public void FreshReplayBufferMapsMarkersAgainstActualSavedHistory()
    {
        var events = new[] { new AutoClipEvent("killstreak", "Killstreak ×57", Event) };
        var source = new SpotifySourceWindow(MonotonicClock.ToSharedSeconds(Event.AddSeconds(-8)), 14, MonotonicClock.BootId);
        var marker = Assert.Single(ClipEventMarkerMapping.FromSavedWindow(events,
            Event.AddSeconds(-90), Event.AddSeconds(6), source));
        Assert.Equal(8, marker.OffsetSeconds, precision: 3);
    }

    private static readonly DateTime Event = new(2026, 9, 6, 0, 8, 32, DateTimeKind.Utc);
    private static readonly TimeSpan OneMinuteBuffer = TimeSpan.FromMinutes(1);

    // Asking for more history than the buffer holds would only return whatever is
    // actually there, so the window says what it can honestly deliver.
    [Fact]
    public void TheWindowNeverAsksForMoreHistoryThanTheBufferHolds()
    {
        var (startUtc, endUtc) = AutoClipWindowPolicy.Extend(
            Event.AddSeconds(-8), Event.AddSeconds(6), TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(15), endUtc - startUtc);
    }
}
