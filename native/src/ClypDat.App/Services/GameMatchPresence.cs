using System.Globalization;
using System.Text.Json;

namespace ClypDat.App.Services;

/// <summary>
/// What a game's own telemetry says about the match in progress, phrased for
/// Discord. Only games with a local data feed produce one - League's Live
/// Client Data API and Valve's Game State Integration for CS2 and Dota 2.
/// </summary>
public sealed record GameMatchPresence(
    string GameId,
    string Details,
    string State,
    DateTime? MatchStartedUtc = null,
    string? SmallImageUrl = null,
    string? SmallImageText = null)
{
    // Clock-derived start times move by a second or so between polls. Resending
    // for that jitter would spend Discord's update budget redrawing a timer
    // that already reads right, so small drift keeps the earlier start.
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(5);

    public static GameMatchPresence? Stabilize(GameMatchPresence? previous, GameMatchPresence? next)
    {
        if (previous is null || next is null || previous.GameId != next.GameId) return next;
        if (previous.MatchStartedUtc is { } before && next.MatchStartedUtc is { } after && (after - before).Duration() <= StartTolerance)
            return next with { MatchStartedUtc = before };
        return next;
    }
}

/// <summary>
/// Raises <see cref="Changed"/> only when the match presence actually changes.
/// Shared by the three listeners so each can publish from its own thread.
/// </summary>
public sealed class GameMatchPresencePublisher
{
    private readonly object _gate = new();
    private GameMatchPresence? _current;

    public event EventHandler<GameMatchPresence?>? Changed;

    public GameMatchPresence? Current { get { lock (_gate) return _current; } }

    public void Publish(GameMatchPresence? next)
    {
        lock (_gate)
        {
            next = GameMatchPresence.Stabilize(_current, next);
            if (_current == next) return;
            _current = next;
        }
        Changed?.Invoke(this, next);
    }
}

public static class GameMatchPresenceParser
{
    /// <summary>From League's <c>liveclientdata/allgamedata</c>.</summary>
    public static GameMatchPresence? FromLeague(JsonElement root, DateTime nowUtc)
    {
        if (!Object(root, "activePlayer", out var active) || !Array(root, "allPlayers", out var players)) return null;
        var names = new[] { String(active, "riotId"), String(active, "summonerName"), String(active, "riotIdGameName") }
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        JsonElement? self = null;
        foreach (var player in players.EnumerateArray())
        {
            if (names.Any(name => string.Equals(name, String(player, "riotId"), StringComparison.OrdinalIgnoreCase)
                               || string.Equals(name, String(player, "summonerName"), StringComparison.OrdinalIgnoreCase)))
            {
                self = player;
                break;
            }
        }
        if (self is not { } me) return null;

        var champion = String(me, "championName");
        var mode = Object(root, "gameData", out var gameData) ? LeagueMode(String(gameData, "gameMode")) : null;
        var gameTime = Object(root, "gameData", out gameData) ? Double(gameData, "gameTime") : null;
        var scores = Object(me, "scores", out var scoreElement) ? scoreElement : default;
        var details = Join(champion, mode) ?? "In a match";
        var state = Kda(Int(scores, "kills"), Int(scores, "deaths"), Int(scores, "assists")) ?? "In a match";
        var alias = ChampionAlias(String(me, "rawChampionName"), champion);
        return new GameMatchPresence("league", details, state,
            gameTime is > 0 ? nowUtc - TimeSpan.FromSeconds(gameTime.Value) : null,
            alias is null ? null : $"https://cdn.communitydragon.org/latest/champion/{alias}/square",
            champion);
    }

    /// <summary>
    /// From a CS2 GSI payload already checked to describe the local player.
    /// GSI carries no match clock, so the start is when ClypDat first saw the map.
    /// </summary>
    public static GameMatchPresence? FromCs2(JsonElement root, DateTime? matchStartedUtc)
    {
        if (!Object(root, "map", out var map)) return null;
        var mapName = Cs2MapName(String(map, "name"));
        if (mapName is null) return null;
        var mode = Cs2Mode(String(map, "mode"));
        var player = Object(root, "player", out var playerElement) ? playerElement : default;
        var stats = Object(player, "match_stats", out var statsElement) ? statsElement : default;

        var ct = Object(map, "team_ct", out var ctElement) ? Int(ctElement, "score") : null;
        var t = Object(map, "team_t", out var tElement) ? Int(tElement, "score") : null;
        var team = String(player, "team");
        string? score = null;
        if (ct is { } ctScore && t is { } tScore)
            score = string.Equals(team, "T", StringComparison.OrdinalIgnoreCase) ? $"{tScore}–{ctScore}" : $"{ctScore}–{tScore}";

        var kda = Kda(Int(stats, "kills"), Int(stats, "deaths"), Int(stats, "assists"));
        return new GameMatchPresence("cs2", Join(mapName, mode)!, Join(score, kda) ?? "In a match", matchStartedUtc);
    }

    /// <summary>From a Dota 2 GSI payload.</summary>
    public static GameMatchPresence? FromDota(JsonElement root, DateTime nowUtc)
    {
        if (!Object(root, "map", out var map)) return null;
        var player = Object(root, "player", out var playerElement) ? playerElement : default;
        var hero = Object(root, "hero", out var heroElement) ? heroElement : default;
        var internalName = String(hero, "name");
        var heroName = DotaHeroName(internalName);
        var level = Int(hero, "level");
        // Spectating or watching a replay nests every player under team2/team3;
        // there is no "you" in that payload to describe.
        if (player.ValueKind == JsonValueKind.Object && (player.TryGetProperty("team2", out _) || player.TryGetProperty("team3", out _))) return null;
        var gameState = String(map, "game_state") ?? string.Empty;
        var picking = gameState.EndsWith("HERO_SELECTION", StringComparison.OrdinalIgnoreCase)
                      || gameState.EndsWith("STRATEGY_TIME", StringComparison.OrdinalIgnoreCase);
        if (heroName is null && !picking) return null;

        var details = heroName is null ? "Picking a hero" : level is > 0 ? $"{heroName} · Level {level}" : heroName;
        var state = Kda(Int(player, "kills"), Int(player, "deaths"), Int(player, "assists")) ?? "In a match";
        var clock = Int(map, "clock_time");
        var shortName = DotaShortName(internalName);
        return new GameMatchPresence("dota2", details, state,
            clock is >= 0 ? nowUtc - TimeSpan.FromSeconds(clock.Value) : null,
            shortName is null ? null : $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/{shortName}.png",
            heroName);
    }

    private static string? Kda(int? kills, int? deaths, int? assists) =>
        kills is null || deaths is null || assists is null ? null : $"{kills}/{deaths}/{assists}";

    private static string? Join(string? first, string? second) =>
        (string.IsNullOrWhiteSpace(first), string.IsNullOrWhiteSpace(second)) switch
        {
            (false, false) => $"{first} · {second}",
            (false, true) => first,
            (true, false) => second,
            _ => null
        };

    private static readonly Dictionary<string, string> LeagueModes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CLASSIC"] = "Summoner's Rift", ["ARAM"] = "ARAM", ["URF"] = "URF", ["ARURF"] = "ARURF",
        ["ONEFORALL"] = "One for All", ["CHERRY"] = "Arena", ["NEXUSBLITZ"] = "Nexus Blitz",
        ["ULTBOOK"] = "Ultimate Spellbook", ["PRACTICETOOL"] = "Practice Tool", ["TUTORIAL"] = "Tutorial",
        ["SWIFTPLAY"] = "Swiftplay"
    };

    private static string? LeagueMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? null : LeagueModes.TryGetValue(mode, out var known) ? known : TitleCase(mode);

    // "game_character_displayname_MonkeyKing" is the key Community Dragon wants;
    // the display name ("Wukong", "Kai'Sa") is not a valid path segment.
    private static string? ChampionAlias(string? rawChampionName, string? championName)
    {
        const string prefix = "game_character_displayname_";
        if (rawChampionName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true && rawChampionName.Length > prefix.Length)
            return rawChampionName[prefix.Length..];
        var letters = new string((championName ?? string.Empty).Where(char.IsLetter).ToArray());
        return letters.Length == 0 ? null : letters;
    }

    private static readonly Dictionary<string, string> Cs2Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["competitive"] = "Competitive", ["premier"] = "Premier", ["casual"] = "Casual",
        ["deathmatch"] = "Deathmatch", ["gungameprogressive"] = "Arms Race", ["scrimcomp2v2"] = "Wingman",
        ["training"] = "Training", ["custom"] = "Custom"
    };

    private static string? Cs2Mode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? null : Cs2Modes.TryGetValue(mode, out var known) ? known : TitleCase(mode);

    private static readonly Dictionary<string, string> Cs2Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de_dust2"] = "Dust II", ["de_inferno"] = "Inferno", ["de_mirage"] = "Mirage", ["de_nuke"] = "Nuke",
        ["de_overpass"] = "Overpass", ["de_vertigo"] = "Vertigo", ["de_ancient"] = "Ancient", ["de_anubis"] = "Anubis",
        ["de_train"] = "Train", ["cs_office"] = "Office", ["cs_italy"] = "Italy"
    };

    private static string? Cs2MapName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Cs2Maps.TryGetValue(raw, out var known)) return known;
        // Workshop maps arrive as "workshop/<id>/<name>".
        var name = raw.Split('/')[^1];
        foreach (var prefix in new[] { "de_", "cs_", "ar_", "gd_", "dm_" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { name = name[prefix.Length..]; break; }
        return name.Length == 0 ? null : TitleCase(name);
    }

    // Internal names that do not spell the hero's name.
    private static readonly Dictionary<string, string> DotaHeroes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["antimage"] = "Anti-Mage", ["nevermore"] = "Shadow Fiend", ["zuus"] = "Zeus", ["wisp"] = "Io",
        ["furion"] = "Nature's Prophet", ["obsidian_destroyer"] = "Outworld Destroyer", ["magnataur"] = "Magnus",
        ["rattletrap"] = "Clockwerk", ["shredder"] = "Timbersaw", ["skeleton_king"] = "Wraith King",
        ["treant"] = "Treant Protector", ["doom_bringer"] = "Doom", ["life_stealer"] = "Lifestealer",
        ["queenofpain"] = "Queen of Pain", ["windrunner"] = "Windranger", ["necrolyte"] = "Necrophos",
        ["centaur"] = "Centaur Warrunner", ["abyssal_underlord"] = "Underlord", ["vengefulspirit"] = "Vengeful Spirit",
        ["keeper_of_the_light"] = "Keeper of the Light", ["sand_king"] = "Sand King", ["drow_ranger"] = "Drow Ranger",
        ["nyx_assassin"] = "Nyx Assassin", ["shadow_shaman"] = "Shadow Shaman", ["crystal_maiden"] = "Crystal Maiden",
        ["storm_spirit"] = "Storm Spirit", ["faceless_void"] = "Faceless Void", ["phantom_assassin"] = "Phantom Assassin",
        ["templar_assassin"] = "Templar Assassin", ["night_stalker"] = "Night Stalker", ["spirit_breaker"] = "Spirit Breaker",
        ["skywrath_mage"] = "Skywrath Mage", ["monkey_king"] = "Monkey King", ["dark_willow"] = "Dark Willow",
        ["primal_beast"] = "Primal Beast", ["marci"] = "Marci", ["muerta"] = "Muerta", ["ringmaster"] = "Ringmaster",
        ["kez"] = "Kez"
    };

    private static string? DotaShortName(string? internalName)
    {
        const string prefix = "npc_dota_hero_";
        return internalName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true && internalName.Length > prefix.Length
            ? internalName[prefix.Length..]
            : null;
    }

    private static string? DotaHeroName(string? internalName) =>
        DotaShortName(internalName) is not { } shortName ? null
        : DotaHeroes.TryGetValue(shortName, out var known) ? known
        : TitleCase(shortName.Replace('_', ' '));

    private static string TitleCase(string value) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());

    private static bool Object(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object) return true;
        value = default;
        return false;
    }

    private static bool Array(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array) return true;
        value = default;
        return false;
    }

    private static string? String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? Double(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
}
