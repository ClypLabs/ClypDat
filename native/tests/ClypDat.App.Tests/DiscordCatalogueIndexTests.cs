using System.Text;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DiscordCatalogueIndexTests
{
    [Fact]
    public async Task IndexKeepsFirstCataloguePositionForEveryKey()
    {
        const string json = """
            [
              { "id": "1", "name": "Counter-Strike 2", "icon_hash": "a", "aliases": ["CS2"],
                "third_party_skus": [{ "distributor": "steam", "id": "730" }],
                "executables": [{ "name": "game/bin/win64/cs2.exe" }] },
              { "id": "2", "name": "cs2", "icon_hash": "b" },
              { "id": "3", "name": null },
              { "id": "", "name": "No id" }
            ]
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var index = await DiscordDetectableGameCatalog.BuildIndexAsync(stream);

        Assert.Equal(3, index.Games.Count);
        Assert.Equal(0, index.BySteamAppId["730"]);
        Assert.Equal(0, index.ByName["counterstrike2"]);
        // "cs2" is both game 1's alias/executable and game 2's name: the first
        // catalogue entry keeps it, as the old first-match scan did.
        Assert.Equal(0, index.ByName["cs2"]);
        Assert.False(index.ByName.ContainsKey("noid"));
    }
}
