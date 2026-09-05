using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using System.Text.Json;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AutoClipCatalogTests
{
    [Fact]
    public void CvGamesExposeTheirRuntimeDeliveryAndDefaultDisabledState()
    {
        var fortnite = AutoClipCatalog.Get("fortnite");
        var helldivers = AutoClipCatalog.Get("helldivers2");

        Assert.True(fortnite.UsesDetectorPack);
        Assert.False(helldivers.UsesDetectorPack);
        Assert.True(helldivers.UsesDetector);
        Assert.False(fortnite.DefaultEnabled);
        Assert.False(helldivers.DefaultEnabled);
        Assert.Equal("clypdat-cv", fortnite.ProviderId);
        Assert.Equal("clypdat-cv", helldivers.ProviderId);
        Assert.Equal("epic-fortnite", fortnite.PortraitDetectionKey);
        Assert.Equal("steam-553850", helldivers.PortraitDetectionKey);
        Assert.Equal("HELLDIVERS™ 2", helldivers.PortraitDisplayName);
        Assert.Equal("HELLDIVERS™ 2", helldivers.Name);
    }

    [Fact]
    public void CvDefaultsAndDominancePrioritiesMatchPackContract()
    {
        var fortnite = AutoClipCatalog.Get("fortnite");
        var defaults = fortnite.Events.Where(item => item.DefaultEnabled).Select(item => item.Id).ToHashSet();

        Assert.Equal(new HashSet<string> { "distance-shot", "impossible-shot", "double-elimination", "multi-elimination", "top-3", "victory-royale" }, defaults);
        Assert.True(fortnite.Events.Single(item => item.Id == "impossible-shot").Priority > fortnite.Events.Single(item => item.Id == "distance-shot").Priority);
        Assert.True(fortnite.Events.Single(item => item.Id == "multi-elimination").Priority > fortnite.Events.Single(item => item.Id == "double-elimination").Priority);
        Assert.True(fortnite.Events.Single(item => item.Id == "victory-royale").Priority > fortnite.Events.Single(item => item.Id == "match-complete").Priority);

        var helldivers = AutoClipCatalog.Get("helldivers2");
        Assert.False(helldivers.Events.Single(item => item.Id == "eliminated").DefaultEnabled);
        Assert.All(helldivers.Events.Where(item => item.Id.StartsWith("killstreak-", StringComparison.Ordinal)), item => Assert.True(item.DefaultEnabled));
        var successfulMission = helldivers.Events.Single(item => item.Id == "successful-mission");
        Assert.True(successfulMission.DefaultEnabled);
        Assert.Equal("missions", successfulMission.GroupId);
        Assert.Equal(15, successfulMission.LeadSeconds);
        Assert.Equal(10, successfulMission.TailSeconds);
    }

    // The settings list renders the catalog in order, and every tile now comes
    // from GamePortraitService rather than a bundled cover - so a new entry
    // without a portrait key would render a blank box.
    [Fact]
    public void ActiveGamesAreAlphabeticalAndAllResolveAPortrait()
    {
        var names = AutoClipCatalog.Active.Select(game => game.Name).ToArray();

        Assert.Equal(names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(), names);
        Assert.All(AutoClipCatalog.Active, game => Assert.False(string.IsNullOrWhiteSpace(game.PortraitDetectionKey)));
    }

    [Fact]
    public void OverwatchShipsAsADetectorPackGameThatIsNotInstalledYet()
    {
        var overwatch = AutoClipCatalog.Get("overwatch");

        Assert.True(overwatch.UsesDetectorPack);
        Assert.False(overwatch.DefaultEnabled);
        Assert.Equal("overwatch", overwatch.PackId);
        Assert.Equal("clypdat-cv", overwatch.ProviderId);
        Assert.Equal("steam-2357570", overwatch.PortraitDetectionKey);
        Assert.Equal("Overwatch®", overwatch.PortraitDisplayName);
        Assert.Equal(
            new HashSet<string> { "multikill", "team-kill", "play-of-the-game" },
            overwatch.Events.Where(item => item.DefaultEnabled).Select(item => item.Id).ToHashSet());
        Assert.Equal("overwatch", AutoClipCatalog.MatchGame(null, "Overwatch.exe", null));
    }

    [Theory]
    [InlineData("FortniteClient-Win64-Shipping.exe", "fortnite")]
    [InlineData("helldivers2.exe", "helldivers2")]
    [InlineData("cs2.exe", "cs2")]
    public void DetectionAliasesResolveStableGameIds(string executable, string expected)
    {
        Assert.Equal(expected, AutoClipCatalog.MatchGame(null, executable, null));
    }

    [Fact]
    public void ProtocolVersionsAndDetectorTransportLimitsArePinned()
    {
        Assert.Equal(2, CaptureWorkerProtocol.Version);
        Assert.Equal(1, DetectorHostProtocol.Version);
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
