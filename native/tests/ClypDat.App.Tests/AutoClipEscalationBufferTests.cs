using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AutoClipEscalationBufferTests
{
    private static readonly DateTime Origin = new(2026, 9, 6, 0, 8, 32, DateTimeKind.Utc);

    // Real windows are 6s and 20s. The tests drive the same code on a compressed
    // clock so the suite does not spend a minute waiting for quiet.
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(20);

    private static AutoClipEscalationBuffer Buffer(out List<AutoClipRequest> ready, out List<string> pending)
    {
        var definition = AutoClipCatalog.Get("overwatch");
        var buffer = new AutoClipEscalationBuffer(definition.Id, definition.Name, definition.Events, Quiet, Max);
        var readyList = new List<AutoClipRequest>();
        var pendingList = new List<string>();
        buffer.Ready += (_, request) => readyList.Add(request);
        buffer.Pending += (_, message) => pendingList.Add(message);
        ready = readyList;
        pending = pendingList;
        return buffer;
    }

    private static AutoClipDetectorEvent Event(string eventId, double atSecond)
    {
        var definition = AutoClipCatalog.Get("overwatch").Events.Single(item => item.Id == eventId);
        return new AutoClipDetectorEvent("overwatch", eventId, definition.Name, $"{eventId}-{atSecond}",
            0.9, Origin.AddSeconds(atSecond), definition.LeadSeconds, definition.TailSeconds);
    }

    // The ask, and what the tester's own clips show: their real ladder ran
    // DOUBLE KILL, TRIPLE KILL, QUADRUPLE KILL 1-3.5s apart, and the old code
    // saved three separate clips of the same fight.
    [Fact]
    public void AnEscalatingStreakBecomesOneClipAtTheTierItReached()
    {
        using var buffer = Buffer(out var ready, out _);

        buffer.Offer(Event("double-kill", 0));
        buffer.Offer(Event("triple-kill", 2));
        buffer.Offer(Event("quadruple-kill", 3.5));

        Assert.Empty(ready);
        Assert.True(SpinWait.SpinUntil(() => ready.Count > 0, TimeSpan.FromSeconds(5)));
        var request = Assert.Single(ready);
        Assert.Equal("quadruple-kill", request.EventId);
        Assert.Equal("Quadruple Kill", request.EventType);
    }

    // An Elimination is a rung too: it must be allowed to grow into the Double
    // Kill that followed it rather than clipping on its own.
    [Fact]
    public void AnEliminationThatBecomesADoubleKillClipsAsTheDoubleKill()
    {
        using var buffer = Buffer(out var ready, out _);

        buffer.Offer(Event("elimination", 0));
        buffer.Offer(Event("double-kill", 1));

        Assert.True(SpinWait.SpinUntil(() => ready.Count > 0, TimeSpan.FromSeconds(5)));
        Assert.Equal("double-kill", Assert.Single(ready).EventId);
    }

    // The window has to cover the whole streak: the opening kill's lead through
    // the last tier's tail, or the clip starts after the fight began.
    [Fact]
    public void TheWindowSpansTheFirstLeadToTheLastTail()
    {
        using var buffer = Buffer(out var ready, out _);
        var catalog = AutoClipCatalog.Get("overwatch");
        var elimination = catalog.Events.Single(item => item.Id == "elimination");
        var quadruple = catalog.Events.Single(item => item.Id == "quadruple-kill");

        buffer.Offer(Event("elimination", 0));
        buffer.Offer(Event("quadruple-kill", 4));

        Assert.True(SpinWait.SpinUntil(() => ready.Count > 0, TimeSpan.FromSeconds(5)));
        var request = Assert.Single(ready);
        Assert.Equal(Origin.AddSeconds(-elimination.LeadSeconds), request.StartUtc);
        Assert.Equal(Origin.AddSeconds(4 + quadruple.TailSeconds), request.EndUtc);
    }

    // A tier that does not improve on what is pending must not re-toast: the
    // notification is "your clip is being made", not one per detection.
    [Fact]
    public void OnlyAnImprovingTierAnnouncesItself()
    {
        using var buffer = Buffer(out _, out var pending);

        buffer.Offer(Event("triple-kill", 0));
        buffer.Offer(Event("elimination", 1));
        buffer.Offer(Event("quadruple-kill", 2));

        Assert.Equal(2, pending.Count);
        Assert.Contains("Triple Kill", pending[0]);
        Assert.Contains("Quadruple Kill", pending[1]);
    }

    // Play of the Game has no group in the catalog because it is its own moment,
    // arriving after the match with a 15s lead. Folding it into a fight that
    // happened seconds earlier would mislabel the fight and stretch its window.
    [Fact]
    public void PlayOfTheGameFiresOnItsOwnWithoutWaiting()
    {
        using var buffer = Buffer(out var ready, out _);

        buffer.Offer(Event("play-of-the-game", 0));

        var request = Assert.Single(ready);
        Assert.Equal("play-of-the-game", request.EventId);
    }

    [Fact]
    public void AStreakStillRunningAtTheMaxWindowIsCutLoose()
    {
        using var buffer = Buffer(out var ready, out _);

        buffer.Offer(Event("double-kill", 0));
        buffer.Offer(Event("triple-kill", 30));

        // The second event is past the 20s cap, so the first streak closes on the
        // spot rather than absorbing a fight half a minute later, and the late one
        // starts a window of its own.
        Assert.Equal("double-kill", ready[0].EventId);
        Assert.True(SpinWait.SpinUntil(() => ready.Count > 1, TimeSpan.FromSeconds(5)));
        Assert.Equal("triple-kill", ready[1].EventId);
    }

    // A window left open when the player switches games must not flush into the
    // next one.
    [Fact]
    public void ResetDropsAnOpenWindowWithoutClipping()
    {
        using var buffer = Buffer(out var ready, out _);

        buffer.Offer(Event("double-kill", 0));
        buffer.Reset();

        Assert.False(SpinWait.SpinUntil(() => ready.Count > 0, TimeSpan.FromSeconds(2)));
    }
}
