using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FortniteDetectorTests
{
    private const string LocalPlayer = "Arashii ッ";

    private static FortniteFrameObservation Frame(
        int second, string killFeed = "", string banner = "", string upperCentre = "") =>
        new(TimeSpan.FromSeconds(second), killFeed, banner, upperCentre);

    private static FortniteDetector Detector()
    {
        var detector = new FortniteDetector();
        detector.SetLocalPlayer(LocalPlayer);
        return detector;
    }

    private static (string Id, string Label)[] Observe(FortniteDetector detector, FortniteFrameObservation frame) =>
        detector.Observe(frame).Select(item => (item.EventId, item.Label)).ToArray();

    // The exact line Fortnite writes at login - the only place the display name
    // appears, and the whole basis for telling your kills from everyone else's.
    [Fact]
    public void DisplayNameIsReadFromTheLoginLine()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ClypDat-FortniteIdentityTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllLines(Path.Combine(folder, "FortniteGame.log"), new[]
            {
                "[2026.09.05-08.32.47:892][  0]LogInit: User: Arashii",
                "[2026.09.05-08.33.01:158][331]LogOnlineAccount: Display: [OnlineAccount:index=2:uid=0][process_user_login] Successfully logged in user. UserId=[0b90e0b1] DisplayName=[Arashii ッ] EpicAccountId=[MCP:0b90e0b1]",
                "[2026.09.05-08.33.01:529][341]LogEOSVoiceChat: SetInputDeviceId effective device Id=[] DisplayName=[Microphone (SteelSeries Alias)]"
            });

            // The voice-chat line also carries DisplayName=[...] and must not win.
            Assert.Equal("Arashii ッ", FortniteIdentity.Resolve(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void TheMostRecentLoginWins()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ClypDat-FortniteIdentityTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllLines(Path.Combine(folder, "FortniteGame.log"), new[]
            {
                "Successfully logged in user. UserId=[a] DisplayName=[OldAccount] EpicAccountId=[MCP:a]",
                "Successfully logged in user. UserId=[b] DisplayName=[NewAccount] EpicAccountId=[MCP:b]"
            });

            Assert.Equal("NewAccount", FortniteIdentity.Resolve(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // OCR drops or mangles the small katakana depending on the frame, so the
    // comparison has to survive it.
    [Fact]
    public void NameMatchingSurvivesWhatOcrDoesToDecoratedNames()
    {
        Assert.True(FortniteIdentity.IsLocalPlayer("Arashii ッ", LocalPlayer));
        Assert.True(FortniteIdentity.IsLocalPlayer("Arashii", LocalPlayer));
        Assert.True(FortniteIdentity.IsLocalPlayer("ARASHII ツ", LocalPlayer));
        Assert.False(FortniteIdentity.IsLocalPlayer("ArashiiTwo", LocalPlayer));
        Assert.False(FortniteIdentity.IsLocalPlayer("geez2n3yz", LocalPlayer));
        // No resolved name means nothing can be attributed.
        Assert.False(FortniteIdentity.IsLocalPlayer("Arashii", null));
    }

    [Fact]
    public void OnlyYourOwnFeedLinesCount()
    {
        const string feed = """
            Arashii ッ (138) eliminated UltimateSteve26 (76) with a rifle (64 m)
            Anonymous[304] knocked out FreshFishBoy with a pistol
            geez2n3yz eliminated SkidzBahh (74) with a pistol
            """;

        var kills = FortniteDetector.ParseOwnEliminations(feed, LocalPlayer);

        Assert.Single(kills);
        Assert.Equal(64, kills[0].Metres);
    }

    // Fortnite prints "(N m)" only on kills far enough away to be worth
    // mentioning, so its presence is the event - no threshold of ours.
    [Fact]
    public void OnlyLongRangeKillsCarryADistance()
    {
        var kills = FortniteDetector.ParseOwnEliminations(
            "Arashii ッ (138) eliminated Anonymous[331] with a rifle", LocalPlayer);

        Assert.Single(kills);
        Assert.Null(kills[0].Metres);
    }

    [Fact]
    public void KnockedOutIsNotAnElimination()
    {
        Assert.Empty(FortniteDetector.ParseOwnEliminations(
            "Arashii ッ (138) knocked out Anonymous[331] with a rifle", LocalPlayer));
        Assert.Empty(FortniteDetector.ParseOwnEliminations(
            "Arashii ッ marked an enemy location", LocalPlayer));
    }

    [Fact]
    public void ADistanceKillRaisesBothTheEliminationAndTheDistanceShot()
    {
        var events = Observe(Detector(), Frame(1,
            killFeed: "Arashii ッ (138) eliminated UltimateSteve26 (76) with a rifle (64 m)"));

        Assert.Contains(("eliminated-player", "Eliminated Player"), events);
        // The tile title carries the distance the game printed.
        Assert.Contains(("distance-shot", "Distance Shot (64 M)"), events);
    }

    [Fact]
    public void AFeedLineFiresOnceWhileItLingers()
    {
        var detector = Detector();
        const string feed = "Arashii ッ (138) eliminated Anonymous[331] with a rifle";

        Assert.NotEmpty(Observe(detector, Frame(1, killFeed: feed)));
        Assert.Empty(Observe(detector, Frame(2, killFeed: feed)));
    }

    // Without a resolved name the feed cannot be attributed, so it stays quiet
    // rather than clipping other players' kills.
    [Fact]
    public void FeedEventsAreSilentWithoutADisplayName()
    {
        var detector = new FortniteDetector();
        detector.SetLocalPlayer(null);

        Assert.Empty(Observe(detector, Frame(1,
            killFeed: "Arashii ッ (138) eliminated UltimateSteve26 (76) with a rifle (64 m)")));
    }

    [Theory]
    [InlineData("ELIMINATION!\nBongodrum010", "eliminated-player", false)]
    [InlineData("DOUBLE ELIM!\nFishingEvryDay", "double-elimination", true)]
    [InlineData("TRIPLE ELIM!\nSomebody", "multi-elimination", true)]
    [InlineData("ENEMY TEAM WIPED!\nDOUBLE ELIM!\nFishingEvryDay", "enemy-team-wiped", true)]
    public void BannerPhrasesMapToStreakEvents(string banner, string expected, bool present)
    {
        var events = Observe(Detector(), Frame(1, banner: banner)).Select(item => item.Id).ToArray();

        if (present) Assert.Contains(expected, events);
        else Assert.DoesNotContain(expected, events);
    }

    [Fact]
    public void TheTileKeepsTheTierTheGamePrinted()
    {
        var events = Observe(Detector(), Frame(1, banner: "TRIPLE ELIM!\nSomebody"));

        Assert.Contains(("multi-elimination", "Triple Elimination"), events);
    }

    [Fact]
    public void VictoryAndDefeatComeFromTheSharedUpperCentreRegion()
    {
        var win = Detector();
        Observe(win, Frame(1, upperCentre: "#1 VICTORY ROYALE"));
        Assert.Contains("victory-royale", Observe(win, Frame(2, upperCentre: "#1 VICTORY ROYALE")).Select(item => item.Id));

        var loss = Detector();
        Observe(loss, Frame(1, upperCentre: "ELIMINATED BY GEE2N3YZ"));
        Assert.Contains("got-eliminated", Observe(loss, Frame(2, upperCentre: "ELIMINATED BY GEE2N3YZ")).Select(item => item.Id));
    }

    [Fact]
    public void EveryDetectedEventIdExistsInTheCatalog()
    {
        var catalog = AutoClipCatalog.Get("fortnite").Events.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(
            new[] { "eliminated-player", "got-eliminated", "distance-shot", "double-elimination", "multi-elimination", "enemy-team-wiped", "victory-royale" },
            id => Assert.Contains(id, catalog));
    }

    [Fact]
    public void FortniteShipsRegionsAndABuiltInDetector()
    {
        var fortnite = AutoClipCatalog.Get("fortnite");

        Assert.True(fortnite.UsesDetector);
        Assert.False(fortnite.UsesDetectorPack);
        Assert.NotNull(DetectorRegions.ForGame("fortnite"));
    }
}
