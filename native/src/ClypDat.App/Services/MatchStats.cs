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
    // Fortnite outfit worn by the recording player, from the replay's
    // recorder PlayerState. The asset ID is independent of their display name.
    public string? OutfitId { get; init; }
    public string? LocalSteamId { get; init; }
    public string? LocalTeamId { get; init; }
    // Rounds before the first side swap; 0 when unknown.
    public int HalftimeAfterRound { get; init; }
    public IReadOnlyList<MatchTeam> Teams { get; init; } = [];
    public IReadOnlyList<MatchPlayer> Players { get; init; } = [];
    public IReadOnlyList<MatchRound> Rounds { get; init; } = [];
    public IReadOnlyList<MatchEvent> Events { get; init; } = [];

    // Battle royale (Fortnite, from its local replay): where the player's
    // team finished, out of how many players, and how the field shrank. Event
    // DemoSeconds are replay seconds; StartedUtc is the replay's start.
    public int? Placement { get; init; }
    public int? PlayerCount { get; init; }
    public string? Playlist { get; init; }
    // From the playlist only; null when it could not be read.
    public bool? ZeroBuild { get; init; }
    public bool Won { get; init; }
    // Replay seconds at which the player's match ended (final elimination,
    // or the win).
    public double? LocalEndSeconds { get; init; }
    public int? PlayersLeftAtEnd { get; init; }
    public IReadOnlyList<MatchCountStep> PlayersLeft { get; init; } = [];
    // The file the summary was read from (name|size|write time), so it is
    // only read again when that file changes.
    public string? SourceKey { get; init; }
    // The playlist is read in a second, costlier pass that waits until no
    // game is running; true until it has had its turn.
    public bool PlaylistPending { get; init; }
    // Fortnite summary events and account matching can be absent independently.
    // Other game providers retain their existing complete-data behaviour.
    public bool HasMatchSummary { get; init; } = true;
    public bool LocalEventsAvailable { get; init; } = true;
    public bool ReplayDetailsIncomplete { get; init; }

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
    // Fortnite's own end-of-match numbers for the recording player.
    public int? Knocks { get; init; }
    // Shots hit over shots fired, 0-100.
    public double? AccuracyPercent { get; init; }
    public int? DamageTaken { get; init; }
    public int? StructureDamage { get; init; }
    public int? MaterialsGathered { get; init; }
    public int? MaterialsUsed { get; init; }
    public double? DistanceMetres { get; init; }
    public int? Revives { get; init; }

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

/// <summary>From <see cref="Seconds"/> (replay time) on, <see cref="Count"/> players are left.</summary>
public sealed record MatchCountStep(double Seconds, int Count);

// Kind: "kill", "death", "assist", "round_end", "bomb_planted",
// "bomb_defused", "bomb_exploded"; Fortnite adds "knock" (you knocked
// someone), "knocked" (you were knocked) and "victory". Times are whichever
// the source had: GSI gives OccurredUtc (+ SharedSeconds), a demo or replay
// gives DemoSeconds.
public sealed record MatchEvent
{
    public string Kind { get; init; } = string.Empty;
    public int Round { get; init; }
    public bool Headshot { get; init; }
    public bool? Won { get; init; }
    public DateTime? OccurredUtc { get; init; }
    public double? SharedSeconds { get; init; }
    public double? DemoSeconds { get; init; }
    // Fortnite: the other player was a bot; the elimination ended your match.
    public bool Bot { get; init; }
    public bool Final { get; init; }
}
