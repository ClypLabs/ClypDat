using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class OfficialGameArtServiceTests
{
    private static readonly OfficialGameArtService Service = new(
    [
        new("helldivers-2.png", "https://www.playstation.com/", ["steam-553850"], ["HELLDIVERS™ 2"])
    ]);

    [Fact]
    public void Resolve_DetectionKey_ReturnsClypDatHostedAsset() =>
        Assert.Equal("https://raw.githubusercontent.com/ClypLabs/ClypDat/master/native/official-game-art/helldivers-2.png", Service.Resolve("steam-553850", "anything"));

    [Fact]
    public void Resolve_DisplayNameAlias_ReturnsClypDatHostedAsset() =>
        Assert.Equal("https://raw.githubusercontent.com/ClypLabs/ClypDat/master/native/official-game-art/helldivers-2.png", Service.Resolve("legacy-key", "HELLDIVERS™ 2"));

    [Fact]
    public void Resolve_Miss_ReturnsNull() => Assert.Null(Service.Resolve("steam-0", "Unknown game"));

    [Fact]
    public void ResolveGameProfileUrl_DetectionKey_ReturnsDiscordGameRoute() =>
        Assert.Equal("https://discord.com/games/1205090671527071784",
            new OfficialGameArtService([
                new("helldivers-2.png", "https://cdn.discordapp.com/app-icons/1205090671527071784/icon.png", ["steam-553850"], [])
            ]).ResolveGameProfileUrl("steam-553850", "HELLDIVERS™ 2"));

    [Fact]
    public async Task ResolveAsync_PackagedManifest_UsesApprovedAssetOnly()
    {
        var image = await OfficialGameArtService.ResolveAsync("riot-valorant", "VALORANT");

        Assert.Equal("https://raw.githubusercontent.com/ClypLabs/ClypDat/master/native/official-game-art/valorant-discord.png", image);
        Assert.Equal("https://raw.githubusercontent.com/ClypLabs/ClypDat/master/native/official-game-art/helldivers-2-discord.png",
            await OfficialGameArtService.ResolveAsync("steam-553850", "HELLDIVERS™ 2"));
    }
}
