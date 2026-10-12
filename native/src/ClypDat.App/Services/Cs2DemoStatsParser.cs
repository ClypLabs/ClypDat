using DemoFile;
using DemoFile.Game.Cs;

namespace ClypDat.App.Services;

// Reads a decompressed CS2 .dem with DemoFile.Net and feeds the scoreboard
// maths. Only game events and the player controllers are looked at. The
// file is streamed from disk rather than loaded: a full match is 100+ MB.
internal static class Cs2DemoStatsParser
{
    public static async Task<MatchStats> ParseAsync(string demoPath, string? localSteamId, CancellationToken cancellationToken)
    {
        var demo = new CsDemoParser();
        var stats = new Cs2DemoStatsAccumulator();
        string? map = null;
        var live = false;
        double Now() => demo.Elapsed.TotalSeconds;
        bool Counting() => live && demo.GameRules?.WarmupPeriod != true;
        static string Side(CCSPlayerController? player) => player?.CSTeamNum switch
        {
            CSTeamNumber.CounterTerrorist => "CT",
            CSTeamNumber.Terrorist => "T",
            _ => string.Empty
        };
        static string? KeyOf(CCSPlayerController? player) =>
            player is null ? null : Cs2DemoStatsAccumulator.Key(player.SteamID, player.PlayerName ?? string.Empty);

        void SeeAll()
        {
            foreach (var player in demo.Players)
            {
                if (player.SteamID == 0 && string.IsNullOrEmpty(player.PlayerName)) continue;
                stats.PlayerSeen(KeyOf(player)!, player.PlayerName ?? string.Empty, player.SteamID, Side(player), player.Score, player.MVPs);
            }
        }

        demo.Source1GameEvents.RoundAnnounceMatchStart += _ => { live = true; stats.MatchStarted(); };
        demo.Source1GameEvents.BeginNewMatch += _ => { live = true; stats.MatchStarted(); };
        demo.Source1GameEvents.RoundStart += _ =>
        {
            // A demo that starts after the announce (rare, but recordings do
            // get cut) is still worth reading from its first live round.
            if (!live && demo.GameRules is { WarmupPeriod: false }) { live = true; stats.MatchStarted(); }
            if (Counting()) { stats.RoundStarted(); SeeAll(); }
        };
        demo.Source1GameEvents.PlayerHurt += e =>
        {
            if (!Counting() || KeyOf(e.Player) is not { } victim) return;
            stats.Hurt(KeyOf(e.Attacker), e.Attacker is null ? null : Side(e.Attacker), victim, Side(e.Player), e.DmgHealth, e.Health, e.Weapon ?? string.Empty);
        };
        demo.Source1GameEvents.PlayerDeath += e =>
        {
            if (!Counting() || KeyOf(e.Player) is not { } victim) return;
            stats.PlayerDied(Now(), victim, Side(e.Player), KeyOf(e.Attacker), e.Attacker is null ? null : Side(e.Attacker),
                KeyOf(e.Assister), e.Assister is null ? null : Side(e.Assister), e.Assistedflash, e.Headshot, localSteamId);
        };
        demo.Source1GameEvents.RoundMvp += e => { if (Counting() && KeyOf(e.Player) is { } key) stats.Mvp(key); };
        demo.Source1GameEvents.BombPlanted += _ => { if (Counting()) stats.Bomb(Now(), "bomb_planted"); };
        demo.Source1GameEvents.BombDefused += _ => { if (Counting()) stats.Bomb(Now(), "bomb_defused"); };
        demo.Source1GameEvents.BombExploded += _ => { if (Counting()) stats.Bomb(Now(), "bomb_exploded"); };
        demo.Source1GameEvents.RoundEnd += e =>
        {
            if (!Counting()) return;
            var winner = e.Winner switch { 3 => "CT", 2 => "T", _ => null };
            var roster = demo.Players.Where(player => Side(player).Length > 0)
                .Select(player => (KeyOf(player)!, Side(player))).ToArray();
            SeeAll();
            stats.RoundEnded(Now(), winner, e.Reason, roster);
        };

        await using var stream = new FileStream(demoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var reader = DemoFileReader.Create(demo, stream);
        await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        map ??= demo.ServerInfo?.MapName;
        SeeAll();
        return stats.Build(map, localSteamId, demo.Elapsed.TotalSeconds);
    }
}
