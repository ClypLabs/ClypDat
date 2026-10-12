using System.Text.Json.Serialization;

namespace ClypDat.App.Services;

// One match's scoreboard, kept game-agnostic so the editor's Stats panel can
// show any game that fills it in. CS2 writes it from two sources: Game State
// Integration while the match is played (local player only, but stamped with
// this PC's clocks), and later the match demo (all ten players, timed in
// demo seconds). A match holding both is "merged": demo numbers, GSI times.
public sealed record MatchStats
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public string Id { get; init; } = string.Empty;
    public string Game { get; init; } = string.Empty;
    public string? Map { get; init; }
    public string? Mode { get; init; }
    // Wall clock. Demo-only matches take these from the match's own start time
    // and the demo's length, which is why they are only trusted coarsely.
    public DateTime StartedUtc { get; init; }
    public DateTime EndedUtc { get; init; }
    // The boot GSI timestamps were taken in. A clip saved in the same boot maps
    // SharedSeconds through its .source.json exactly; across boots only the
    // wall clock is left.
    public string? BootId { get; init; }
    public bool HasLiveData { get; init; }
    public bool HasDemoData { get; init; }
    // Null until the demo is looked at: "parsed", "expired" or "failed".
    public string? DemoStatus { get; init; }
    public string? MatchId { get; init; }
    public double? DemoDurationSeconds { get; init; }
    public string? LocalPlayerName { get; init; }
    public string? LocalSteamId { get; init; }
    public string? LocalTeamId { get; init; }
    // Rounds before the first side swap; 0 when unknown.
    public int HalftimeAfterRound { get; init; }
    public IReadOnlyList<MatchTeam> Teams { get; init; } = [];
    public IReadOnlyList<MatchPlayer> Players { get; init; } = [];
    public IReadOnlyList<MatchRound> Rounds { get; init; } = [];
    public IReadOnlyList<MatchEvent> Events { get; init; } = [];

    [JsonIgnore] public MatchPlayer? LocalPlayer => Players.FirstOrDefault(player => player.IsLocal);
    [JsonIgnore] public MatchTeam? LocalTeam => Teams.FirstOrDefault(team => team.Id == LocalTeamId);
    [JsonIgnore] public MatchTeam? OpposingTeam => Teams.FirstOrDefault(team => team.Id != LocalTeamId);
}

public sealed record MatchTeam(string Id, string Name, int Score);

public sealed record MatchPlayer
{
    public string Name { get; init; } = string.Empty;
    public string? SteamId { get; init; }
    public string TeamId { get; init; } = string.Empty;
    public bool IsLocal { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int? Mvps { get; init; }
    public int? Score { get; init; }
    public int RoundsPlayed { get; init; }
    // Null where the source cannot know: GSI reports no damage at all.
    public int? Damage { get; init; }
    public int? HeadshotKills { get; init; }
    public int? UtilityDamage { get; init; }
    public int? FlashAssists { get; init; }
    public int? KastRounds { get; init; }
    public int? EntryKills { get; init; }

    [JsonIgnore] public double? Adr => Damage is { } damage && RoundsPlayed > 0 ? (double)damage / RoundsPlayed : null;
    // Headshot kills over kills, as Leetify counts it (not shots or hits).
    [JsonIgnore] public double? HeadshotPercent => HeadshotKills is { } hs && Kills > 0 ? 100.0 * hs / Kills : HeadshotKills is not null ? 0 : null;
    [JsonIgnore] public double? KastPercent => KastRounds is { } kast && RoundsPlayed > 0 ? 100.0 * kast / RoundsPlayed : null;
}

public sealed record MatchRound
{
    public int Number { get; init; }
    public string? WinnerTeamId { get; init; }
    // The side that won, for games with sides ("CT" / "T").
    public string? WinnerSide { get; init; }
    // Normalised: "elimination", "bomb", "defuse", "time", "surrender", "draw".
    public string? Reason { get; init; }
    public bool? LocalWon { get; init; }
    public string? LocalSide { get; init; }
    public int LocalKills { get; init; }
    public int LocalHeadshots { get; init; }
    public bool BombPlanted { get; init; }
    public bool BombDefused { get; init; }
    public bool BombExploded { get; init; }
    public DateTime? EndedUtc { get; init; }
    public double? DemoSeconds { get; init; }
}

// Kind: "kill", "death", "assist", "round_end", "bomb_planted",
// "bomb_defused", "bomb_exploded". Times are whichever the source had: GSI
// gives OccurredUtc (+ SharedSeconds), a demo gives DemoSeconds.
public sealed record MatchEvent
{
    public string Kind { get; init; } = string.Empty;
    public int Round { get; init; }
    public bool Headshot { get; init; }
    public bool? Won { get; init; }
    public DateTime? OccurredUtc { get; init; }
    public double? SharedSeconds { get; init; }
    public double? DemoSeconds { get; init; }
}
