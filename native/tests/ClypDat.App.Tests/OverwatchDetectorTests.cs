using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class OverwatchDetectorTests
{
    private static OverwatchFrameObservation Frame(
        int second, string leftColumn = "", string killFeed = "", string teamKill = "", params DetectedBanner[] banners) =>
        new(TimeSpan.FromSeconds(second), leftColumn, killFeed, teamKill, banners);

    private static DetectedBanner Banner(string eventId, string label, double score = 0.9) => new(eventId, label, score);

    private static string[] Observe(OverwatchDetector detector, OverwatchFrameObservation frame) =>
        detector.Observe(frame).Select(item => item.EventId).ToArray();

    // Windows OCR cannot read these banners at all, so they arrive already
    // recognised by GrayTemplateMatcher rather than as text.
    [Theory]
    [InlineData("double-kill", "Double Kill")]
    public void EachStreakTierFiresFromItsBannerMatch(string eventId, string label)
    {
        var detector = new OverwatchDetector();

        Observe(detector, Frame(1, banners: Banner(eventId, label)));

        Assert.Contains(eventId, Observe(detector, Frame(2, banners: Banner(eventId, label))));
    }

    // The banner sits on screen for many sampled frames; without the latch one
    // streak would fire an event twice a second for its whole duration.
    [Fact]
    public void AStreakFiresOnceWhileItsBannerStaysUp()
    {
        var detector = new OverwatchDetector();

        var triple = Banner("triple-kill", "Triple Kill");
        Observe(detector, Frame(1, banners: triple));
        Assert.Contains("triple-kill", Observe(detector, Frame(2, banners: triple)));
        Assert.Empty(Observe(detector, Frame(3, banners: triple)));
        Assert.Empty(Observe(detector, Frame(4, banners: triple)));
    }

    [Fact]
    public void EveryPlayOfTheGameClipsEvenWhenItIsNotYours()
    {
        var detector = new OverwatchDetector();

        Observe(detector, Frame(1, leftColumn: "PLAY OF THE GAME GOWONSS AS FREJA"));

        Assert.Contains("play-of-the-game", Observe(detector, Frame(2, leftColumn: "PLAY OF THE GAME GOWONSS AS FREJA")));
    }

    // A tester's kill cam: the killer's Double then Triple Kill under the
    // stylised "ELIMINATED BY" label, which OCR cannot read. Nothing fires -
    // not the streaks, not a Team Kill, not a Play of the Game.
    [Fact]
    public void NothingFiresDuringAKillCam()
    {
        var detector = new OverwatchDetector();
        var eliminatedBy = Banner(OverwatchDetector.EliminatedByEventId, "Eliminated By");

        var events = new List<string>();
        events.AddRange(Observe(detector, Frame(1, killFeed: "YOU WERE ELIMINATED BY BUMBO")));
        // The kill cam opens before its label fades in.
        events.AddRange(Observe(detector, Frame(2, banners: Banner("double-kill", "Double Kill"))));
        events.AddRange(Observe(detector, Frame(3, banners: [eliminatedBy, Banner("double-kill", "Double Kill")])));
        events.AddRange(Observe(detector, Frame(4, killFeed: "XENKO 88", banners: [eliminatedBy, Banner("triple-kill", "Triple Kill"), Banner("team-kill", "Team Kill")])));
        events.AddRange(Observe(detector, Frame(5, killFeed: "XENKO 88", banners: [eliminatedBy, Banner("triple-kill", "Triple Kill"), Banner("team-kill", "Team Kill"), Banner("play-of-the-game", "Play of the Game")])));
        events.AddRange(Observe(detector, Frame(6, banners: [eliminatedBy, Banner("play-of-the-game", "Play of the Game")])));

        Assert.Empty(events);
    }

    // Once the hold runs out the player is back, and their own streaks count.
    [Fact]
    public void StreaksCountAgainAfterTheKillCam()
    {
        var detector = new OverwatchDetector();

        Observe(detector, Frame(1, banners: Banner(OverwatchDetector.EliminatedByEventId, "Eliminated By")));
        for (var second = 2; second < 12; second++) Observe(detector, Frame(second));

        var triple = Banner("triple-kill", "Triple Kill");
        Observe(detector, Frame(12, banners: triple));
        Assert.Contains("triple-kill", Observe(detector, Frame(13, banners: triple)));
    }

    [Fact]
    public void EliminationRowsParseNameAndDamageAndIgnoreSaves()
    {
        Assert.Equal(new[] { "XENKO 88" }, OverwatchDetector.ParseEliminations("XENKO 88"));
        Assert.Equal(new[] { "R4V4G3R 25", "ALEX 25" }, OverwatchDetector.ParseEliminations("TRIPLE KILL\nR4V4G3R 25\nALEX 25"));
        Assert.Empty(OverwatchDetector.ParseEliminations("SAVED BY COMRADEDOGGO"));
        Assert.Empty(OverwatchDetector.ParseEliminations("DOUBLE KILL"));
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
}
