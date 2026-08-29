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
}
