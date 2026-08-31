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
    public async Task CreateActivity_OfficialGameImage_UsesClypDatOverlay()
    {
        var officialImage = await OfficialGameArtService.ResolveAsync("riot-valorant", "VALORANT");
        var activity = DiscordRichPresenceService.CreateActivity(
            new DiscordPresence(
                "Recording VALORANT",
                "Ready to clip",
                DateTime.UtcNow,
                officialImage,
                "VALORANT"),
            showGetClypDatButton: false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(activity));
        var assets = document.RootElement.GetProperty("assets");
        Assert.Equal(officialImage, assets.GetProperty("large_image").GetString());
        Assert.Equal("VALORANT", assets.GetProperty("large_text").GetString());
        Assert.Equal("https://www.clypdat.xyz/icon.png", assets.GetProperty("small_image").GetString());
        Assert.Equal("Clipping with ClypDat", assets.GetProperty("small_text").GetString());
    }

}
