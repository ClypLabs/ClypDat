using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class OverwatchDetectorTests
{
    private static OverwatchFrameObservation Frame(
        int second, string leftColumn = "", string killFeed = "", string teamKill = "") =>
        new(TimeSpan.FromSeconds(second), leftColumn, killFeed, teamKill);

    private static string[] Observe(OverwatchDetector detector, OverwatchFrameObservation frame) =>
        detector.Observe(frame).Select(item => item.EventId).ToArray();

    [Theory]
    [InlineData("DOUBLE KILL", "double-kill")]
    [InlineData("TRIPLE KILL", "triple-kill")]
    [InlineData("QUADRUPLE KILL", "quadruple-kill")]
    [InlineData("QUINTUPLE KILL", "quintuple-kill")]
    public void EachStreakTierFiresOnItsOwnPhrase(string phrase, string expected)
    {
        var detector = new OverwatchDetector();

        Assert.Contains(expected, Observe(detector, Frame(1, killFeed: phrase)));
    }

    // The banner sits on screen for many sampled frames; without the latch one
    // streak would fire an event twice a second for its whole duration.
    [Fact]
    public void AStreakFiresOnceWhileItsBannerStaysUp()
    {
        var detector = new OverwatchDetector();

        Assert.Contains("triple-kill", Observe(detector, Frame(1, killFeed: "TRIPLE KILL")));
        Assert.Empty(Observe(detector, Frame(2, killFeed: "TRIPLE KILL")));
        Assert.Empty(Observe(detector, Frame(3, killFeed: "TRIPLE KILL")));
    }

    [Fact]
    public void TeamKillFiresFromItsOwnRegion()
    {
        var detector = new OverwatchDetector();

        Assert.Contains("team-kill", Observe(detector, Frame(1, teamKill: "TEAM KILL!")));
    }

    // The whole point of the left column: during a Play of the Game the HUD
    // belongs to the featured player, and their kills are not yours. Verified
    // against session A @ 51:53, which produces a quadruple and a quintuple
    // inside somebody else's replay.
    [Fact]
    public void StreaksInsideAPlayOfTheGameAreIgnored()
    {
        var detector = new OverwatchDetector();

        // The POTG latch wants two consecutive frames before it commits, so a
        // single OCR misread cannot invent a clip.
        Observe(detector, Frame(1, leftColumn: "PLAY OF THE GAME GOWONSS", killFeed: "QUADRUPLE KILL"));
        var events = Observe(detector, Frame(2, leftColumn: "PLAY OF THE GAME GOWONSS", killFeed: "QUADRUPLE KILL"));

        Assert.Contains("play-of-the-game", events);
        Assert.DoesNotContain("quadruple-kill", events);
        Assert.DoesNotContain("elimination", events);
    }

    [Fact]
    public void EveryPlayOfTheGameClipsEvenWhenItIsNotYours()
    {
        var detector = new OverwatchDetector();

        Observe(detector, Frame(1, leftColumn: "PLAY OF THE GAME GOWONSS AS FREJA"));

        Assert.Contains("play-of-the-game", Observe(detector, Frame(2, leftColumn: "PLAY OF THE GAME GOWONSS AS FREJA")));
    }

    [Theory]
    [InlineData("ELIMINATED BY D.MON GROINCANCER")]
    [InlineData("YOU ARE NOW DEATH SPECTATING: EGG")]
    public void NothingFiresWhileWatchingThePlayerWhoKilledYou(string leftColumn)
    {
        var detector = new OverwatchDetector();

        var events = Observe(detector, Frame(1, leftColumn, killFeed: "TRIPLE KILL\nXENKO 88", teamKill: "TEAM KILL!"));

        Assert.Empty(events);
    }

    // A streak that was on screen when the replay began must not fire the
    // instant the replay ends.
    [Fact]
    public void AStreakHeldOverFromASpectatedReplayDoesNotFireOnResume()
    {
        var detector = new OverwatchDetector();

        Observe(detector, Frame(1, leftColumn: "PLAY OF THE GAME", killFeed: "TRIPLE KILL"));
        Observe(detector, Frame(2, leftColumn: "PLAY OF THE GAME", killFeed: "TRIPLE KILL"));

        Assert.Empty(Observe(detector, Frame(3, killFeed: string.Empty)));
    }

    [Fact]
    public void EliminationRowsParseNameAndDamageAndIgnoreSaves()
    {
        Assert.Equal(new[] { "XENKO 88" }, OverwatchDetector.ParseEliminations("XENKO 88"));
        Assert.Equal(new[] { "R4V4G3R 25", "ALEX 25" }, OverwatchDetector.ParseEliminations("TRIPLE KILL\nR4V4G3R 25\nALEX 25"));
        Assert.Empty(OverwatchDetector.ParseEliminations("SAVED BY COMRADEDOGGO"));
        Assert.Empty(OverwatchDetector.ParseEliminations("DOUBLE KILL"));
    }

    [Fact]
    public void TheSameEliminationRowDoesNotFireTwiceWhileItLingers()
    {
        var detector = new OverwatchDetector();

        Assert.Contains("elimination", Observe(detector, Frame(1, killFeed: "XENKO 88")));
        Assert.Empty(Observe(detector, Frame(2, killFeed: "XENKO 88")));
    }

    [Fact]
    public void IsSpectatingCoversEveryBorrowedHudState()
    {
        Assert.True(OverwatchDetector.IsSpectating("PLAY OF THE GAME"));
        Assert.True(OverwatchDetector.IsSpectating("ELIMINATED BY D.MON"));
        Assert.True(OverwatchDetector.IsSpectating("YOU ARE NOW DEATH SPECTATING: EGG"));
        Assert.False(OverwatchDetector.IsSpectating(string.Empty));
        Assert.False(OverwatchDetector.IsSpectating("DEFEND OBJECTIVE A"));
    }

    // Every event the detector can raise has to exist in the catalog, or it
    // would be filtered out by the enabled-event check and never clip.
    [Fact]
    public void EveryDetectedEventIdExistsInTheCatalog()
    {
        var catalog = AutoClipCatalog.Get("overwatch").Events.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var detected = new[]
        {
            "double-kill", "triple-kill", "quadruple-kill", "quintuple-kill",
            "team-kill", "play-of-the-game", "elimination"
        };

        Assert.All(detected, id => Assert.Contains(id, catalog));
    }

    [Fact]
    public void OverwatchShipsRegionsAndABuiltInDetector()
    {
        var overwatch = AutoClipCatalog.Get("overwatch");

        Assert.True(overwatch.UsesDetector);
        Assert.False(overwatch.UsesDetectorPack);
        Assert.NotNull(DetectorRegions.ForGame("overwatch"));
        Assert.NotNull(DetectorRegions.ForGame("helldivers2"));
        Assert.Null(DetectorRegions.ForGame("cs2"));
    }
}
