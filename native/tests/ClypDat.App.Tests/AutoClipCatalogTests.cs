using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using System.Text.Json;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AutoClipCatalogTests
{
    [Fact]
    public void CvDefaultsAndDominancePrioritiesMatchPackContract()
    {
        var fortnite = AutoClipCatalog.Get("fortnite");
        var defaults = fortnite.Events.Where(item => item.DefaultEnabled).Select(item => item.Id).ToHashSet();

        Assert.Equal(new HashSet<string> { "distance-shot", "double-elimination", "multi-elimination", "enemy-team-wiped", "victory-royale" }, defaults);
        Assert.True(fortnite.Events.Single(item => item.Id == "multi-elimination").Priority > fortnite.Events.Single(item => item.Id == "double-elimination").Priority);
        Assert.True(fortnite.Events.Single(item => item.Id == "enemy-team-wiped").Priority > fortnite.Events.Single(item => item.Id == "multi-elimination").Priority);
        Assert.True(fortnite.Events.Single(item => item.Id == "victory-royale").Priority > fortnite.Events.Single(item => item.Id == "enemy-team-wiped").Priority);
        // Trimmed to events that are a discrete, clippable on-screen moment.
        // "Top 3" was a threshold on the players-remaining counter - a state,
        // not a moment - so it clipped whatever happened to be on screen when
        // the number changed, which was usually nothing. The rest were either
        // progression popups rather than gameplay, or had no distinct moment
        // of their own to catch.
        Assert.All(
            new[] { "top-3", "impossible-shot", "match-complete", "bounty-complete", "quest-complete", "headshot" },
            id => Assert.DoesNotContain(fortnite.Events, item => item.Id == id));
        // CS2 keeps its own headshot event - it reads one from the game state
        // integration rather than off the screen, so the Fortnite removal is
        // about detectability, not the event being unwanted everywhere.
        Assert.Contains(AutoClipCatalog.Get("cs2").Events, item => item.Id == "headshot");

        var helldivers = AutoClipCatalog.Get("helldivers2");
        Assert.False(helldivers.Events.Single(item => item.Id == "eliminated").DefaultEnabled);
        var streak = Assert.Single(helldivers.Events, item => item.Id.StartsWith("killstreak", StringComparison.Ordinal));
        Assert.True(streak.DefaultEnabled);
        Assert.Equal("killstreak", streak.Id);
        Assert.Null(streak.GroupId);
        Assert.Contains("20+", streak.Description);
        Assert.Contains("one second", streak.Description);
        var successfulMission = helldivers.Events.Single(item => item.Id == "successful-mission");
        Assert.True(successfulMission.DefaultEnabled);
        Assert.Equal("missions", successfulMission.GroupId);
        Assert.Equal(15, successfulMission.LeadSeconds);
        Assert.Equal(10, successfulMission.TailSeconds);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    public void LegacyKillstreakSettingsMigrateToOneToggle(bool twenty, bool fifty, bool hundred, bool expected)
    {
        var events = new Dictionary<string, bool>
        {
            ["killstreak-20"] = twenty, ["killstreak-50"] = fifty, ["killstreak-100"] = hundred, ["eliminated"] = false
        };
        AutoClipCatalog.MigrateHelldiversKillstreakSetting(events);
        Assert.Equal(expected, events["killstreak"]);
        Assert.False(events["eliminated"]);
        Assert.Equal(2, events.Count);
        events["killstreak"] = !expected;
        AutoClipCatalog.MigrateHelldiversKillstreakSetting(events);
        Assert.Equal(!expected, events["killstreak"]);
    }

    [Fact]
    public void ProtocolVersionsAndDetectorTransportLimitsArePinned()
    {
        // 9 records clip-relative input and camera source mappings.
        // replay duration rather than retained media timing.
        // older install is rejected rather than answering without it.
        // 11 carries the overlay recording mode (editable layers or burned in).
        // 12 adds live Full Session state and closed-file events.
        Assert.Equal(12, CaptureWorkerProtocol.Version);
        // 3 adds the per-slot sequence to reject torn or recycled detector frames.
        Assert.Equal(3, DetectorHostProtocol.Version);
        Assert.Equal(3, DetectorHostProtocol.FrameSlotCount);
        Assert.Equal(10, DetectorHostProtocol.MaximumFramesPerSecond);
        Assert.Equal(512L * 1024 * 1024, DetectorHostProtocol.MaximumWorkingSetBytes);
    }

    [Fact]
    public void LegacyClipInfoRemainsReadableWithNewMetadataUnset()
    {
        const string json = """{"GameDisplayName":"Fortnite","AutoClipEventType":"Victory Royale"}""";
        var info = JsonSerializer.Deserialize<ClipInfo>(json);

        Assert.NotNull(info);
        Assert.Equal("Victory Royale", info.AutoClipEventType);
        Assert.Null(info.AutoClipProviderId);
        Assert.Null(info.AutoClipEventIds);
        Assert.Null(info.AutoClipPlanId);
    }
}
