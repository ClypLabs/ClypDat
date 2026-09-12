using System.Text.Json;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GameMatchPresenceTests
{
    private static readonly DateTime Now = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void League_DescribesTheActivePlayer()
    {
        var root = Parse("""
        {
          "activePlayer": { "riotId": "Me#OCE" },
          "allPlayers": [
            { "riotId": "Someone#NA1", "championName": "Garen", "rawChampionName": "game_character_displayname_Garen", "scores": { "kills": 1, "deaths": 1, "assists": 1 } },
            { "riotId": "Me#OCE", "championName": "Wukong", "rawChampionName": "game_character_displayname_MonkeyKing", "scores": { "kills": 7, "deaths": 2, "assists": 9 } }
          ],
          "gameData": { "gameMode": "CLASSIC", "gameTime": 600.4 }
        }
        """);

        var presence = GameMatchPresenceParser.FromLeague(root, Now)!;

        Assert.Equal("league", presence.GameId);
        Assert.Equal("Wukong · Summoner's Rift", presence.Details);
        Assert.Equal("7/2/9", presence.State);
        Assert.Equal(Now - TimeSpan.FromSeconds(600.4), presence.MatchStartedUtc);
        Assert.Equal("https://cdn.communitydragon.org/latest/champion/MonkeyKing/square", presence.SmallImageUrl);
        Assert.Equal("Wukong", presence.SmallImageText);
    }

    [Fact]
    public void League_WithoutTheActivePlayerInTheList_ReturnsNull()
    {
        var root = Parse("""{ "activePlayer": { "riotId": "Me#OCE" }, "allPlayers": [], "gameData": { "gameMode": "ARAM" } }""");
        Assert.Null(GameMatchPresenceParser.FromLeague(root, Now));
    }

    [Fact]
    public void Cs2_PutsTheLocalTeamsScoreFirst()
    {
        var started = Now.AddMinutes(-12);
        var root = Parse("""
        {
          "map": { "name": "de_mirage", "mode": "competitive", "team_ct": { "score": 6 }, "team_t": { "score": 8 } },
          "player": { "team": "T", "match_stats": { "kills": 12, "deaths": 5, "assists": 3 } }
        }
        """);

        var presence = GameMatchPresenceParser.FromCs2(root, started)!;

        Assert.Equal("Mirage · Competitive", presence.Details);
        Assert.Equal("8–6 · 12/5/3", presence.State);
        Assert.Equal(started, presence.MatchStartedUtc);
        Assert.Null(presence.SmallImageUrl);
    }

    [Fact]
    public void Cs2_InTheMenu_ReturnsNull()
    {
        Assert.Null(GameMatchPresenceParser.FromCs2(Parse("""{ "player": { "activity": "menu" } }"""), null));
    }

    [Fact]
    public void Dota_NamesTheHeroAndUsesTheMatchClock()
    {
        var root = Parse("""
        {
          "map": { "clock_time": 754, "game_state": "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" },
          "player": { "kills": 7, "deaths": 2, "assists": 9 },
          "hero": { "name": "npc_dota_hero_antimage", "level": 14 }
        }
        """);

        var presence = GameMatchPresenceParser.FromDota(root, Now)!;

        Assert.Equal("Anti-Mage · Level 14", presence.Details);
        Assert.Equal("7/2/9", presence.State);
        Assert.Equal(Now - TimeSpan.FromSeconds(754), presence.MatchStartedUtc);
        Assert.Equal("https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/antimage.png", presence.SmallImageUrl);
    }

    [Fact]
    public void Dota_BeforeTheHornHasNoTimer()
    {
        var root = Parse("""{ "map": { "clock_time": -45, "game_state": "DOTA_GAMERULES_STATE_PRE_GAME" }, "player": { "kills": 0, "deaths": 0, "assists": 0 }, "hero": { "name": "npc_dota_hero_sand_king", "level": 1 } }""");
        var presence = GameMatchPresenceParser.FromDota(root, Now)!;
        Assert.Equal("Sand King · Level 1", presence.Details);
        Assert.Null(presence.MatchStartedUtc);
    }

    [Fact]
    public void Dota_Spectating_ReturnsNull()
    {
        var root = Parse("""{ "map": { "clock_time": 300, "game_state": "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" }, "player": { "team2": { "player0": { "kills": 3 } } }, "hero": { "team2": {} } }""");
        Assert.Null(GameMatchPresenceParser.FromDota(root, Now));
    }

    [Fact]
    public void Publisher_IgnoresTimerJitterAndRepeats()
    {
        var publisher = new GameMatchPresencePublisher();
        var raised = new List<GameMatchPresence?>();
        publisher.Changed += (_, presence) => raised.Add(presence);

        var first = new GameMatchPresence("league", "Ahri · ARAM", "1/0/2", Now);
        publisher.Publish(first);
        publisher.Publish(first with { MatchStartedUtc = Now.AddSeconds(1.5) });
        publisher.Publish(first with { State = "2/0/2", MatchStartedUtc = Now.AddSeconds(-1) });
        publisher.Publish(null);

        Assert.Equal(3, raised.Count);
        Assert.Equal(Now, raised[1]!.MatchStartedUtc);
        Assert.Equal("2/0/2", raised[1]!.State);
        Assert.Null(raised[2]);
    }
}
