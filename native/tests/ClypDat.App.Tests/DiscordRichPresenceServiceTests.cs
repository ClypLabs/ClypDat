using System.Text.Json;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DiscordRichPresenceServiceTests
{
    [Fact]
    public void CreateActivity_EmptyState_OmitsStateField()
    {
        var activity = DiscordRichPresenceService.CreateActivity(
            new DiscordPresence("Recording HELLDIVERS 2", string.Empty, DateTime.UtcNow),
            showGetClypDatButton: false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(activity));
        Assert.Equal("Recording HELLDIVERS 2", document.RootElement.GetProperty("details").GetString());
        Assert.False(document.RootElement.TryGetProperty("state", out _));
    }

    [Fact]
    public void CreateActivity_EmptyPresence_ReturnsNullForClear()
    {
        Assert.Null(DiscordRichPresenceService.CreateActivity(DiscordPresence.None, showGetClypDatButton: false));
    }

    [Fact]
    public void CreateActivity_ButtonToggles_KeepPresenceFieldsAndRemoveButton()
    {
        var presence = new DiscordPresence("Recording DOOM", "Replay ready", new DateTime(2026, 9, 8, 1, 2, 3, DateTimeKind.Utc));

        using var on = JsonDocument.Parse(JsonSerializer.Serialize(
            DiscordRichPresenceService.CreateActivity(presence, showGetClypDatButton: true)));
        using var off = JsonDocument.Parse(JsonSerializer.Serialize(
            DiscordRichPresenceService.CreateActivity(presence, showGetClypDatButton: false)));
        using var onAgain = JsonDocument.Parse(JsonSerializer.Serialize(
            DiscordRichPresenceService.CreateActivity(presence, showGetClypDatButton: true)));

        Assert.True(on.RootElement.TryGetProperty("buttons", out var buttons));
        Assert.Equal("Get ClypDat", buttons[0].GetProperty("label").GetString());
        Assert.False(off.RootElement.TryGetProperty("buttons", out _));
        Assert.True(onAgain.RootElement.TryGetProperty("buttons", out _));
        Assert.Equal(on.RootElement.GetProperty("details").GetString(), off.RootElement.GetProperty("details").GetString());
        Assert.Equal(on.RootElement.GetProperty("state").GetString(), off.RootElement.GetProperty("state").GetString());
        Assert.Equal(on.RootElement.GetProperty("timestamps").GetProperty("start").GetInt64(),
            off.RootElement.GetProperty("timestamps").GetProperty("start").GetInt64());
    }

    [Fact]
    public async Task CreateActivity_OfficialGameImage_UsesGameArtWithoutOverlay()
    {
        var officialImage = await OfficialGameArtService.ResolveAsync("riot-valorant", "VALORANT");
        var activity = DiscordRichPresenceService.CreateActivity(
            new DiscordPresence(
                "Recording VALORANT",
                "Ready to clip",
                DateTime.UtcNow,
                officialImage,
                "VALORANT",
                "https://discord.com/games/700136079562375258"),
            showGetClypDatButton: false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(activity));
        var assets = document.RootElement.GetProperty("assets");
        Assert.Equal(officialImage, assets.GetProperty("large_image").GetString());
        Assert.Equal("VALORANT", assets.GetProperty("large_text").GetString());
        Assert.Equal("https://discord.com/games/700136079562375258", assets.GetProperty("large_url").GetString());
        Assert.False(assets.TryGetProperty("small_image", out _));
        Assert.False(assets.TryGetProperty("small_text", out _));
    }

    [Fact]
    public void CreateActivity_MatchSmallImage_AddsCornerBadge()
    {
        var activity = DiscordRichPresenceService.CreateActivity(
            new DiscordPresence("Ahri · Summoner's Rift", "7/2/9", DateTime.UtcNow,
                SmallImageUrl: "https://cdn.communitydragon.org/latest/champion/Ahri/square", SmallImageText: "Ahri"),
            showGetClypDatButton: false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(activity));
        var assets = document.RootElement.GetProperty("assets");
        Assert.Equal("clypdat", assets.GetProperty("large_image").GetString());
        Assert.Equal("https://cdn.communitydragon.org/latest/champion/Ahri/square", assets.GetProperty("small_image").GetString());
        Assert.Equal("Ahri", assets.GetProperty("small_text").GetString());
    }
}
