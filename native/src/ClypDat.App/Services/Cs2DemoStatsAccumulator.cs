namespace ClypDat.App.Services;

// The scoreboard maths for a CS2 demo, kept apart from the parser so it can
// be tested with plain events. Players are keyed by Steam ID (bots, which
// have none, by name). Sides are "CT" / "T" as they were at that moment;
// teams are named after the side each player finished on, because scores
// in CS travel with the team through the halftime swap.
internal sealed class Cs2DemoStatsAccumulator
{
    private const double TradeWindowSeconds = 5;
    private static readonly HashSet<string> UtilityWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "hegrenade", "molotov", "incgrenade", "inferno", "weapon_hegrenade", "weapon_molotov", "weapon_incgrenade"
    };

    private sealed class Player
    {
        public string Name = string.Empty;
        public string? SteamId;
        public string Side = string.Empty;
        public int Kills, Deaths, Assists, Damage, HeadshotKills, UtilityDamage, FlashAssists, KastRounds, EntryKills, Mvps, RoundsPlayed;
        public int? Score;
    }

    private sealed record Death(double Seconds, string Victim, string VictimSide, string? Killer);

    private readonly Dictionary<string, Player> _players = new();
    private readonly Dictionary<string, int> _health = new();
    private readonly List<Death> _roundDeaths = new();
    private readonly HashSet<string> _roundKillers = new();
    private readonly HashSet<string> _roundAssisters = new();
    private readonly List<MatchRound> _rounds = new();
    private readonly List<(int Round, IReadOnlyList<string> Winners)> _roundWinners = new();
    private readonly List<(string Key, MatchEvent Event)> _events = new();
    private readonly Dictionary<int, (bool Planted, bool Defused, bool Exploded)> _bomb = new();
    private bool _roundHasKill;

    public int RoundNumber => _rounds.Count + 1;

    public static string Key(ulong steamId, string name) => steamId != 0 ? steamId.ToString() : "bot:" + name;

    /// <summary>The match went live: everything before it was warmup.</summary>
    public void MatchStarted()
    {
        _players.Clear();
        _rounds.Clear();
        _roundWinners.Clear();
        _events.Clear();
        _bomb.Clear();
        RoundStarted();
    }

    public void RoundStarted()
    {
        _health.Clear();
        _roundDeaths.Clear();
        _roundKillers.Clear();
        _roundAssisters.Clear();
        _roundHasKill = false;
    }

    public void PlayerSeen(string key, string name, ulong steamId, string side, int? score = null, int? mvps = null)
    {
        var player = Get(key);
        if (name.Length > 0) player.Name = name;
        if (steamId != 0) player.SteamId = steamId.ToString();
        if (side is "CT" or "T") player.Side = side;
        if (score is { } value) player.Score = value;
        if (mvps is { } mvpCount && mvpCount > player.Mvps) player.Mvps = mvpCount;
    }

    public void Hurt(string? attacker, string? attackerSide, string victim, string victimSide, int damage, int healthAfter, string weapon)
    {
        var before = _health.TryGetValue(victim, out var health) ? health : 100;
        _health[victim] = Math.Max(0, healthAfter);
        if (attacker is null || attacker == victim || attackerSide == victimSide || attackerSide is not ("CT" or "T")) return;
        // Damage past the victim's remaining health is not damage dealt; an
        // AWP body shot into a 20 HP player counts 20, not 115.
        var dealt = Math.Clamp(Math.Min(damage, before), 0, 100);
        var player = Get(attacker);
        player.Damage += dealt;
        if (UtilityWeapons.Contains(weapon)) player.UtilityDamage += dealt;
    }

    public void PlayerDied(double seconds, string victim, string victimSide, string? killer, string? killerSide,
        string? assister, string? assisterSide, bool flashAssist, bool headshot, string? localKey)
    {
        Get(victim).Deaths++;
        var enemyKill = killer is not null && killer != victim && killerSide is "CT" or "T" && killerSide != victimSide;
        _roundDeaths.Add(new Death(seconds, victim, victimSide, enemyKill ? killer : null));
        if (enemyKill)
        {
            var player = Get(killer!);
            player.Kills++;
            if (headshot) player.HeadshotKills++;
            if (!_roundHasKill) player.EntryKills++;
            _roundKillers.Add(killer!);
        }
        _roundHasKill = true;
        if (assister is not null && assister != victim && assisterSide is "CT" or "T" && assisterSide != victimSide)
        {
            var player = Get(assister);
            if (flashAssist) player.FlashAssists++;
            else player.Assists++;
            _roundAssisters.Add(assister);
        }

        if (localKey is null) return;
        if (enemyKill && killer == localKey)
            _events.Add((killer, new MatchEvent { Kind = "kill", Round = RoundNumber, Headshot = headshot, DemoSeconds = seconds }));
        if (victim == localKey)
            _events.Add((victim, new MatchEvent { Kind = "death", Round = RoundNumber, DemoSeconds = seconds }));
        if (assister == localKey && !flashAssist && assisterSide != victimSide)
            _events.Add((assister, new MatchEvent { Kind = "assist", Round = RoundNumber, DemoSeconds = seconds }));
    }

    public void Mvp(string key) => Get(key).Mvps++;

    public void Bomb(double seconds, string kind)
    {
        var flags = _bomb.GetValueOrDefault(RoundNumber);
        if (kind == "bomb_planted") flags.Planted = true;
        if (kind == "bomb_defused") flags.Defused = true;
        if (kind == "bomb_exploded") flags.Exploded = true;
        _bomb[RoundNumber] = flags;
        _events.Add((string.Empty, new MatchEvent { Kind = kind, Round = RoundNumber, DemoSeconds = seconds }));
    }

    /// <summary>Closes the round. Roster is everyone on a side when it ended.</summary>
    public void RoundEnded(double seconds, string? winnerSide, int reason, IReadOnlyList<(string Key, string Side)> roster)
    {
        if (reason is 16 or 0) { RoundStarted(); return; }
        var number = RoundNumber;
        var present = roster.Where(member => member.Side is "CT" or "T").ToList();
        foreach (var (key, side) in present)
        {
            var player = Get(key);
            player.Side = side;
            player.RoundsPlayed++;
            var died = _roundDeaths.FirstOrDefault(death => death.Victim == key);
            var traded = died is not null && died.Killer is { } killer && _roundDeaths.Any(other =>
                other.Victim == killer && other.Killer is not null && other.Seconds >= died.Seconds &&
                other.Seconds - died.Seconds <= TradeWindowSeconds && other.VictimSide != side);
            if (_roundKillers.Contains(key) || _roundAssisters.Contains(key) || died is null || traded) player.KastRounds++;
        }

        var bomb = _bomb.GetValueOrDefault(number);
        var reasonName = ReasonName(reason);
        _rounds.Add(new MatchRound
        {
            Number = number,
            WinnerSide = winnerSide,
            Reason = reasonName,
            DemoSeconds = seconds,
            BombPlanted = bomb.Planted || reasonName is "bomb" or "defuse",
            BombDefused = bomb.Defused || reasonName == "defuse",
            BombExploded = bomb.Exploded || reasonName == "bomb"
        });
        _roundWinners.Add((number, present.Where(member => member.Side == winnerSide).Select(member => member.Key).ToArray()));
        _events.Add((string.Empty, new MatchEvent { Kind = "round_end", Round = number, DemoSeconds = seconds }));
        RoundStarted();
    }

    public MatchStats Build(string? map, string? localSteamId, double durationSeconds)
    {
        var localKey = localSteamId;
        var finalTeam = _players.ToDictionary(pair => pair.Key, pair => pair.Value.Side);
        var rounds = new List<MatchRound>();
        var halftime = 0;
        string? localFirstSide = null;
        foreach (var round in _rounds)
        {
            var winners = _roundWinners.First(item => item.Round == round.Number).Winners;
            // The team, not the side: whichever final team most of the round's
            // winners finished on.
            var team = winners.Select(key => finalTeam.GetValueOrDefault(key)).Where(side => side is "CT" or "T")
                .GroupBy(side => side).OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault();
            string? localSide = null;
            if (localKey is not null && finalTeam.TryGetValue(localKey, out var localTeam))
                localSide = winners.Contains(localKey) ? round.WinnerSide : round.WinnerSide is { } side ? Cs2MatchTracker.Opposite(side) : null;
            if (localSide is not null)
            {
                localFirstSide ??= localSide;
                if (halftime == 0 && localSide != localFirstSide) halftime = round.Number - 1;
            }
            var localKills = _events.Count(item => item.Key == localKey && item.Event.Kind == "kill" && item.Event.Round == round.Number);
            var localHs = _events.Count(item => item.Key == localKey && item.Event.Kind == "kill" && item.Event.Headshot && item.Event.Round == round.Number);
            bool? won = localKey is not null && finalTeam.TryGetValue(localKey, out var lt) && team is not null ? team == lt : null;
            rounds.Add(round with { WinnerTeamId = team, LocalWon = won, LocalSide = localSide, LocalKills = localKills, LocalHeadshots = localHs });
        }

        var events = _events.Select(item => item.Event.Kind == "round_end"
            ? item.Event with { Won = rounds.FirstOrDefault(round => round.Number == item.Event.Round)?.LocalWon }
            : item.Event).ToArray();
        var players = _players.Where(pair => pair.Value.Side is "CT" or "T" && pair.Value.RoundsPlayed > 0).Select(pair => new MatchPlayer
        {
            Name = pair.Value.Name,
            SteamId = pair.Value.SteamId,
            TeamId = pair.Value.Side,
            IsLocal = localKey is not null && pair.Key == localKey,
            Kills = pair.Value.Kills,
            Deaths = pair.Value.Deaths,
            Assists = pair.Value.Assists,
            Mvps = pair.Value.Mvps,
            Score = pair.Value.Score,
            RoundsPlayed = pair.Value.RoundsPlayed,
            Damage = pair.Value.Damage,
            HeadshotKills = pair.Value.HeadshotKills,
            UtilityDamage = pair.Value.UtilityDamage,
            FlashAssists = pair.Value.FlashAssists,
            KastRounds = pair.Value.KastRounds,
            EntryKills = pair.Value.EntryKills
        }).OrderByDescending(player => player.Score ?? player.Kills).ThenByDescending(player => player.Kills).ToArray();
        var local = players.FirstOrDefault(player => player.IsLocal);
        return new MatchStats
        {
            Game = "cs2",
            Map = map,
            Mode = "competitive",
            HasDemoData = true,
            DemoStatus = "parsed",
            DemoDurationSeconds = durationSeconds,
            LocalPlayerName = local?.Name,
            LocalSteamId = localSteamId,
            LocalTeamId = local?.TeamId,
            HalftimeAfterRound = halftime > 0 ? halftime : rounds.Count > 16 ? 12 : 0,
            Teams =
            [
                new MatchTeam("CT", "Counter-Terrorists", rounds.Count(round => round.WinnerTeamId == "CT")),
                new MatchTeam("T", "Terrorists", rounds.Count(round => round.WinnerTeamId == "T"))
            ],
            Players = players,
            Rounds = rounds,
            Events = events
        };
    }

    // CSRoundEndReason as round_end reports it.
    internal static string ReasonName(int reason) => reason switch
    {
        1 => "bomb",
        7 => "defuse",
        8 or 9 => "elimination",
        12 => "time",
        10 => "draw",
        17 or 18 => "surrender",
        _ => "other"
    };

    private Player Get(string key)
    {
        if (!_players.TryGetValue(key, out var player)) _players[key] = player = new Player();
        return player;
    }
}
