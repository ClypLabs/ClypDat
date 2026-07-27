using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class GameCatalogRulesTests
{
    [Fact]
    public void MatcherRequiresEveryPopulatedConstraintFamily()
    {
        var matcher = new GameWindowMatcher
        {
            Executable = "game.exe",
            ClassEquals = new List<string> { "GameWindow" },
            TitleContains = new List<string> { "Arena" },
            MinWidth = 800
        };

        Assert.True(GameCatalogRules.Matches(matcher, "GAME.EXE", "Arena Match", "GameWindow", 1920, 1080));
        Assert.False(GameCatalogRules.Matches(matcher, "game.exe", "Arena Match", "OtherWindow", 1920, 1080));
        Assert.False(GameCatalogRules.Matches(matcher, "game.exe", "Arena Match", "GameWindow", 640, 480));
    }

    [Fact]
    public void ParserRetainsLegacyFlatCatalog()
    {
        var games = GameCatalogRules.Parse("{\"game.exe\":\"My Game\"}");

        var game = Assert.Single(games);
        Assert.Equal("My Game", game.DisplayName);
        Assert.True(GameCatalogRules.Matches(Assert.Single(game.Matchers), "game.exe", "title", "class", 1, 1));
    }

    [Fact]
    public void ParserRejectsUnsupportedSchema()
    {
        Assert.Throws<InvalidDataException>(() => GameCatalogRules.Parse("{\"schemaVersion\":99,\"games\":[]}"));
    }

    [Fact]
    public void CatalogEntryGroupsMultipleExecutablesUnderOneGame()
    {
        var games = GameCatalogRules.Parse("""
            { "schemaVersion": 1, "games": [{ "id": "primary.exe", "displayName": "Fortnite", "matchers": [
                { "executable": "launcher.exe" }, { "executable": "primary.exe" }
            ] }] }
            """);

        var game = Assert.Single(games);
        Assert.All(game.Matchers, matcher => Assert.True(
            GameCatalogRules.Matches(matcher, matcher.Executable, "Title", "Class", 1920, 1080)));
        Assert.Equal("primary.exe", game.Id);
    }
}
