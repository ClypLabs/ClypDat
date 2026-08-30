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
    public void CreateActivity_GameImage_UsesClypDatOverlay()
    {
        var activity = DiscordRichPresenceService.CreateActivity(
            new DiscordPresence(
                "Recording HELLDIVERS 2",
                "Ready to clip",
                DateTime.UtcNow,
                "https://cdn.example.test/helldivers-2.png",
                "HELLDIVERS 2"),
            showGetClypDatButton: false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(activity));
        var assets = document.RootElement.GetProperty("assets");
        Assert.Equal("https://cdn.example.test/helldivers-2.png", assets.GetProperty("large_image").GetString());
        Assert.Equal("HELLDIVERS 2", assets.GetProperty("large_text").GetString());
        Assert.Equal("clypdat", assets.GetProperty("small_image").GetString());
        Assert.Equal("Clipping with ClypDat", assets.GetProperty("small_text").GetString());
    }
}
