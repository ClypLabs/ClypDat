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

    [Fact]
    public void KnockedOutIsNotAnElimination()
    {
        Assert.Empty(FortniteDetector.ParseOwnEliminations(
            "Arashii ッ (138) knocked out Anonymous[331] with a rifle", LocalPlayer));
        Assert.Empty(FortniteDetector.ParseOwnEliminations(
            "Arashii ッ marked an enemy location", LocalPlayer));
    }

    [Fact]
    public void AFeedLineFiresOnceWhileItLingers()
    {
        var detector = Detector();
        const string feed = "Arashii ッ (138) eliminated Anonymous[331] with a rifle";

        Assert.NotEmpty(Observe(detector, Frame(1, killFeed: feed)));
        Assert.Empty(Observe(detector, Frame(2, killFeed: feed)));
    }

    [Theory]
    [InlineData("ELIMINATION!\nBongodrum010", "eliminated-player", false)]
    public void BannerPhrasesMapToStreakEvents(string banner, string expected, bool present)
    {
        var events = Observe(Detector(), Frame(1, banner: banner)).Select(item => item.Id).ToArray();

        if (present) Assert.Contains(expected, events);
        else Assert.DoesNotContain(expected, events);
    }
}
