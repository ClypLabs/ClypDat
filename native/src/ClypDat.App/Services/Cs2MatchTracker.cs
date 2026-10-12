using System.Text.Json;

namespace ClypDat.App.Services;

// Builds one CS2 match summary out of GSI snapshots. GSI has no "match
// ended" message and no per-player scoreboard for a playing client, so this
// watches the map phase and the round history and keeps only what the local
// player's own feed can prove: their K/D/A, the round results, the bomb.
// Snapshots for a spectated teammate (player.steamid != provider.steamid)
// still carry the map, so the round history keeps up while dead.
internal sealed class Cs2MatchTracker
{
    private static readonly HashSet<string> TrackedModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "competitive", "premier", "scrimcomp2v2", "casual"
    };

    private bool _active;
    private bool _finished;
    private string _map = string.Empty;
    private string _mode = string.Empty;
    private string _phase = string.Empty;
    private DateTime _startedUtc;
    private int _currentRound = 1;
    private string? _localName;
    private string? _localSide;
    private int _kills, _deaths, _assists, _mvps, _score;
    private bool _haveStats;
    private int _roundKills, _roundHeadshots;
    private string _roundPhase = string.Empty;
    private string _bomb = string.Empty;
    private int _ctScore, _tScore;
    private readonly SortedDictionary<int, string> _roundWins = new();
    private readonly Dictionary<int, string> _sideByRound = new();
    private readonly Dictionary<int, (int Kills, int Headshots)> _killsByRound = new();
    private readonly Dictionary<int, (bool Planted, bool Defused, bool Exploded)> _bombByRound = new();
    private readonly Dictionary<int, (DateTime Utc, double Shared)> _roundEndTimes = new();
    private readonly List<MatchEvent> _events = new();

    public string? ProviderSteamId { get; private set; }

    /// <summary>Feeds one snapshot. Returns the previous match when this one closed it.</summary>
    public MatchStats? Observe(JsonElement root, DateTime utcNow, double sharedSeconds)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (TryObject(root, "provider", out var provider) && String(provider, "steamid") is { Length: > 0 } providerId)
            ProviderSteamId = providerId;

        if (!TryObject(root, "map", out var map) || String(map, "name") is not { Length: > 0 } mapName)
        {
            // Back at the menu: whatever was being played is over.
            return _active ? Close() : null;
        }

        var mode = String(map, "mode") ?? string.Empty;
        var phase = String(map, "phase") ?? string.Empty;
        MatchStats? closed = null;
        if (_active && (!string.Equals(mapName, _map, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(mode, _mode, StringComparison.OrdinalIgnoreCase)))
            closed = Close();

        if (!_active)
        {
            if (_finished && string.Equals(mapName, _map, StringComparison.OrdinalIgnoreCase) && phase == "gameover") return closed;
            if (!TrackedModes.Contains(mode) || phase is not ("warmup" or "live" or "intermission")) return closed;
            Begin(mapName, mode, utcNow);
        }

        // Warmup kills and rounds are not the match. Its real start is the
        // first live snapshot after it.
        if (_phase == "warmup" && phase == "live") Begin(mapName, mode, utcNow);
        _phase = phase;

        var completedRounds = Int(map, "round") ?? 0;
        if (TryObject(map, "team_ct", out var ct) && Int(ct, "score") is { } ctScore) _ctScore = ctScore;
        if (TryObject(map, "team_t", out var t) && Int(t, "score") is { } tScore) _tScore = tScore;

        var roundPhase = TryObject(root, "round", out var round) ? String(round, "phase") ?? string.Empty : string.Empty;
        if (roundPhase is "freezetime" or "live" && phase != "warmup") _currentRound = completedRounds + 1;

        var isLocal = TryObject(root, "player", out var player) && ProviderSteamId is { } localId &&
                      string.Equals(String(player, "steamid"), localId, StringComparison.Ordinal);
        if (isLocal && phase != "warmup") ObserveLocalPlayer(player, roundPhase, utcNow, sharedSeconds);

        if (phase != "warmup")
        {
            ObserveBomb(round, utcNow, sharedSeconds);
            ObserveRoundWins(map, roundPhase, utcNow, sharedSeconds);
        }
        _roundPhase = roundPhase;

        if (phase == "gameover" && _active)
        {
            var finished = Close();
            _finished = true;
            return finished ?? closed;
        }
        return closed;
    }

    /// <summary>The game went away (listener stopped, CS2 closed).</summary>
    public MatchStats? Finish() => _active ? Close() : null;

    private void Begin(string map, string mode, DateTime utcNow)
    {
        _active = true;
        _finished = false;
        _map = map;
        _mode = mode;
        _startedUtc = utcNow;
        _currentRound = 1;
        _haveStats = false;
        _kills = _deaths = _assists = _mvps = _score = 0;
        _roundKills = _roundHeadshots = 0;
        _bomb = string.Empty;
        _ctScore = _tScore = 0;
        _roundWins.Clear();
        _sideByRound.Clear();
        _killsByRound.Clear();
        _bombByRound.Clear();
        _roundEndTimes.Clear();
        _events.Clear();
    }

    private void ObserveLocalPlayer(JsonElement player, string roundPhase, DateTime utcNow, double sharedSeconds)
    {
        if (String(player, "name") is { Length: > 0 } name) _localName = name;
        if (String(player, "team") is "CT" or "T" && roundPhase is "freezetime" or "live")
        {
            _localSide = String(player, "team");
            _sideByRound[_currentRound] = _localSide!;
        }

        if (TryObject(player, "state", out var state))
        {
            var roundKills = Int(state, "round_kills") ?? 0;
            var roundHeadshots = Int(state, "round_killhs") ?? 0;
            // Counters reset with each round; a drop is a new round, not lost kills.
            if (roundKills < _roundKills) _roundKills = _roundHeadshots = 0;
            for (var kill = _roundKills; kill < roundKills; kill++)
            {
                var headshot = kill - _roundKills < roundHeadshots - _roundHeadshots;
                _events.Add(new MatchEvent { Kind = "kill", Round = _currentRound, Headshot = headshot, OccurredUtc = utcNow, SharedSeconds = sharedSeconds });
            }
            _roundKills = roundKills;
            _roundHeadshots = Math.Max(_roundHeadshots, roundHeadshots);
            var previous = _killsByRound.GetValueOrDefault(_currentRound);
            _killsByRound[_currentRound] = (Math.Max(previous.Kills, roundKills), Math.Max(previous.Headshots, roundHeadshots));
        }

        if (TryObject(player, "match_stats", out var stats))
        {
            var deaths = Int(stats, "deaths") ?? _deaths;
            var assists = Int(stats, "assists") ?? _assists;
            if (_haveStats)
            {
                for (var i = _deaths; i < deaths; i++)
                    _events.Add(new MatchEvent { Kind = "death", Round = _currentRound, OccurredUtc = utcNow, SharedSeconds = sharedSeconds });
                for (var i = _assists; i < assists; i++)
                    _events.Add(new MatchEvent { Kind = "assist", Round = _currentRound, OccurredUtc = utcNow, SharedSeconds = sharedSeconds });
            }
            _kills = Int(stats, "kills") ?? _kills;
            _deaths = deaths;
            _assists = assists;
            _mvps = Int(stats, "mvps") ?? _mvps;
            _score = Int(stats, "score") ?? _score;
            _haveStats = true;
        }
    }

    private void ObserveBomb(JsonElement round, DateTime utcNow, double sharedSeconds)
    {
        var bomb = round.ValueKind == JsonValueKind.Object ? String(round, "bomb") ?? string.Empty : string.Empty;
        if (bomb == _bomb) return;
        // "planted" can be missed while the snapshot is throttled; the end
        // state still proves it was planted.
        var flags = _bombByRound.GetValueOrDefault(_currentRound);
        switch (bomb)
        {
            case "planted":
                flags.Planted = true;
                _events.Add(new MatchEvent { Kind = "bomb_planted", Round = _currentRound, OccurredUtc = utcNow, SharedSeconds = sharedSeconds });
                break;
            case "defused":
                flags.Planted = flags.Defused = true;
                _events.Add(new MatchEvent { Kind = "bomb_defused", Round = _currentRound, OccurredUtc = utcNow, SharedSeconds = sharedSeconds });
                break;
            case "exploded":
                flags.Planted = flags.Exploded = true;
                _events.Add(new MatchEvent { Kind = "bomb_exploded", Round = _currentRound, OccurredUtc = utcNow, SharedSeconds = sharedSeconds });
                break;
        }
        if (bomb.Length > 0) _bombByRound[_currentRound] = flags;
        _bomb = bomb;
    }

    private void ObserveRoundWins(JsonElement map, string roundPhase, DateTime utcNow, double sharedSeconds)
    {
        if (!TryObject(map, "round_wins", out var wins)) return;
        var added = new List<int>();
        foreach (var property in wins.EnumerateObject())
        {
            if (!int.TryParse(property.Name, out var number) || number <= 0 || property.Value.ValueKind != JsonValueKind.String) continue;
            if (_roundWins.TryAdd(number, property.Value.GetString() ?? string.Empty)) added.Add(number);
        }
        // Only a round that ended in front of us has a trustworthy time; a
        // backlog (listener started mid-match) is history without one.
        if (added.Count != 1 || roundPhase != "over") return;
        var ended = added[0];
        _roundEndTimes[ended] = (utcNow, sharedSeconds);
        var winnerSide = SideOf(_roundWins[ended]);
        var localSide = _sideByRound.GetValueOrDefault(ended) ?? _localSide;
        _events.Add(new MatchEvent
        {
            Kind = "round_end", Round = ended, OccurredUtc = utcNow, SharedSeconds = sharedSeconds,
            Won = winnerSide is not null && localSide is not null ? winnerSide == localSide : null
        });
    }

    private MatchStats? Close()
    {
        _active = false;
        if (_roundWins.Count == 0) return null;
        var halftime = DetectHalftime(_sideByRound) ?? DefaultHalftime(_mode);
        var finalSide = _localSide ?? _sideByRound.OrderBy(pair => pair.Key).Select(pair => pair.Value).LastOrDefault();
        var rounds = new List<MatchRound>();
        foreach (var (number, raw) in _roundWins)
        {
            var winnerSide = SideOf(raw);
            var localSide = _sideByRound.GetValueOrDefault(number) ?? InferSide(number, halftime, finalSide, _roundWins.Keys.Max());
            bool? localWon = winnerSide is not null && localSide is not null ? winnerSide == localSide : null;
            var kills = _killsByRound.GetValueOrDefault(number);
            var bomb = _bombByRound.GetValueOrDefault(number);
            var reason = NormaliseReason(raw);
            rounds.Add(new MatchRound
            {
                Number = number,
                WinnerSide = winnerSide,
                WinnerTeamId = localWon is null || finalSide is null ? null : localWon.Value ? finalSide : Opposite(finalSide),
                Reason = reason,
                LocalWon = localWon,
                LocalSide = localSide,
                LocalKills = kills.Kills,
                LocalHeadshots = kills.Headshots,
                BombPlanted = bomb.Planted || reason is "bomb" or "defuse",
                BombDefused = bomb.Defused || reason == "defuse",
                BombExploded = bomb.Exploded || reason == "bomb",
                EndedUtc = _roundEndTimes.TryGetValue(number, out var time) ? time.Utc : null
            });
        }

        var endedUtc = _roundEndTimes.Count > 0 ? _roundEndTimes.Values.Max(time => time.Utc) : _startedUtc;
        if (_events.Count > 0) endedUtc = Max(endedUtc, _events.Max(e => e.OccurredUtc ?? DateTime.MinValue));
        var teams = finalSide is null ? Array.Empty<MatchTeam>() : new[]
        {
            new MatchTeam("CT", "Counter-Terrorists", _ctScore),
            new MatchTeam("T", "Terrorists", _tScore)
        };
        var headshots = rounds.Sum(round => round.LocalHeadshots);
        var players = !_haveStats ? Array.Empty<MatchPlayer>() : new[]
        {
            new MatchPlayer
            {
                Name = _localName ?? "You", SteamId = ProviderSteamId, TeamId = finalSide ?? string.Empty, IsLocal = true,
                Kills = _kills, Deaths = _deaths, Assists = _assists, Mvps = _mvps, Score = _score,
                RoundsPlayed = rounds.Count, HeadshotKills = headshots
            }
        };
        return new MatchStats
        {
            Id = $"cs2-{_startedUtc:yyyyMMddHHmmss}-{SafeId(_map)}",
            Game = "cs2",
            Map = _map,
            Mode = _mode,
            StartedUtc = _startedUtc,
            EndedUtc = endedUtc,
            BootId = MonotonicClock.BootId,
            HasLiveData = true,
            LocalPlayerName = _localName,
            LocalSteamId = ProviderSteamId,
            LocalTeamId = finalSide,
            HalftimeAfterRound = halftime,
            Teams = teams,
            Players = players,
            Rounds = rounds,
            Events = _events.ToArray()
        };
    }

    internal static int? DetectHalftime(IReadOnlyDictionary<int, string> sideByRound)
    {
        string? first = null;
        foreach (var (number, side) in sideByRound.OrderBy(pair => pair.Key))
        {
            first ??= side;
            if (side != first) return number - 1;
        }
        return null;
    }

    internal static int DefaultHalftime(string? mode) =>
        string.Equals(mode, "scrimcomp2v2", StringComparison.OrdinalIgnoreCase) ? 8 : 12;

    // Regulation swaps once at halftime; overtime (MR3) swaps every three rounds.
    internal static string? InferSide(int round, int halftime, string? finalSide, int lastRound)
    {
        if (finalSide is null || halftime <= 0) return null;
        static int SwapsBefore(int round, int halftime)
        {
            if (round <= halftime) return 0;
            if (round <= halftime * 2) return 1;
            return 1 + (round - halftime * 2 - 1) / 3;
        }
        var swaps = SwapsBefore(lastRound, halftime) - SwapsBefore(round, halftime);
        return swaps % 2 == 0 ? finalSide : Opposite(finalSide);
    }

    internal static string? SideOf(string reason) =>
        reason.StartsWith("ct_", StringComparison.OrdinalIgnoreCase) ? "CT"
        : reason.StartsWith("t_", StringComparison.OrdinalIgnoreCase) ? "T" : null;

    internal static string NormaliseReason(string reason)
    {
        var lower = reason.ToLowerInvariant();
        if (lower.Contains("defuse")) return "defuse";
        if (lower.Contains("bomb")) return "bomb";
        if (lower.Contains("time")) return "time";
        if (lower.Contains("surrender")) return "surrender";
        if (lower.Contains("elimination")) return "elimination";
        return "other";
    }

    internal static string Opposite(string side) => side == "CT" ? "T" : "CT";

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static string SafeId(string map) => new(map.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').Take(40).ToArray());

    private static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object) return true;
        value = default;
        return false;
    }

    private static string? String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
