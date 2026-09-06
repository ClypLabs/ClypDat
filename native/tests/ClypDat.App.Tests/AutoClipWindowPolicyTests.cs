using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AutoClipWindowPolicyTests
{
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
