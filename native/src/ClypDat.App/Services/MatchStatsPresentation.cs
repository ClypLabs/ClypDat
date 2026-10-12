using System.Globalization;

namespace ClypDat.App.Services;

public sealed record MatchStatTile(string Label, string Value);
public sealed record MatchRadarAxis(string Label, double Value, string Display, bool Available = true)
{
    public double? AngleDegrees { get; init; }
    // Every game gets a useful clickable label; providers can add the full
    // score calculation without changing the shared chart.
    public string Explanation { get; init; } = Available && double.IsFinite(Value)
        ? $"{Display}. Score: {Math.Clamp(Value, 0, 1) * 100:0}/100."
        : "No stats were recorded for this axis.";
    // Drawn as a labelled spoke with no point: missing data is never a 0.
    public static MatchRadarAxis Unavailable(string label) => new(label, double.NaN, "Not recorded", Available: false);
}
public sealed record MatchRoundCell(int Number, bool? Won, string Reason, bool StartsHalf);

// Turns a MatchStats into what the Stats panel shows. Kept free of Avalonia
// so the numbers can be tested.
public static class MatchStatsPresentation
{
    // Fixed reference ranges, so a radar means the same thing from one match
    // to the next: the low end is a poor game, the high end an excellent one.
    internal static readonly (double Low, double High) AimRange = (10, 65);       // HS %
    internal static readonly (double Low, double High) DamageRange = (40, 120);   // ADR
    internal static readonly (double Low, double High) CombatRange = (0.3, 1.1);  // kills per round
    internal static readonly (double Low, double High) SurvivalRange = (0.2, 0.6); // 1 - deaths per round
    internal static readonly (double Low, double High) UtilityRange = (0, 12);    // utility damage per round

    public static double Normalise(double value, (double Low, double High) range) =>
        double.IsFinite(value) ? Math.Clamp((value - range.Low) / (range.High - range.Low), 0, 1) : 0;

    /// <summary>Five axes, or none without demo data: GSI has no damage to plot.</summary>
    public static IReadOnlyList<MatchRadarAxis> RadarAxes(MatchStats match)
    {
        if (!match.HasDemoData || match.LocalPlayer is not { RoundsPlayed: > 0 } player) return [];
        var rounds = (double)player.RoundsPlayed;
        var hs = player.HeadshotPercent ?? 0;
        var adr = player.Adr ?? 0;
        var kpr = player.Kills / rounds;
        var survival = 1 - player.Deaths / rounds;
        var utility = (player.UtilityDamage ?? 0) / rounds;
        return
        [
            new("Aim", Normalise(hs, AimRange), $"{hs:0}% HS"),
            new("Damage", Normalise(adr, DamageRange), $"{adr:0} ADR"),
            new("Combat", Normalise(kpr, CombatRange), $"{kpr:0.00} KPR"),
            new("Survival", Normalise(survival, SurvivalRange), $"{survival * 100:0}% lived"),
            new("Utility", Normalise(utility, UtilityRange), $"{utility:0.0} UD/R")
        ];
    }

    public static IReadOnlyList<MatchStatTile> Tiles(MatchStats match)
    {
        if (match.LocalPlayer is not { } player) return [];
        var kda = $"{player.Kills}/{player.Deaths}/{player.Assists}";
        if (match.HasDemoData)
            return
            [
                new("ADR", Format(player.Adr, "0")),
                new("HS%", Format(player.HeadshotPercent, "0") + (player.HeadshotPercent is null ? string.Empty : "%")),
                new("K/D/A", kda),
                new("KAST", Format(player.KastPercent, "0") + (player.KastPercent is null ? string.Empty : "%"))
            ];
        return
        [
            new("K/D/A", kda),
            new("HS%", Format(player.HeadshotPercent, "0") + (player.HeadshotPercent is null ? string.Empty : "%")),
            new("MVPs", player.Mvps?.ToString(CultureInfo.CurrentCulture) ?? "–"),
            new("Score", player.Score?.ToString(CultureInfo.CurrentCulture) ?? "–")
        ];
    }

    public static IReadOnlyList<MatchRoundCell> Rounds(MatchStats match)
    {
        var halftime = match.HalftimeAfterRound;
        return match.Rounds.OrderBy(round => round.Number).Select(round => new MatchRoundCell(
            round.Number,
            round.LocalWon,
            ReasonLabel(round.Reason),
            StartsHalf(round.Number, halftime))).ToArray();
    }

    // A gap before the second half and before each half of overtime (MR3).
    internal static bool StartsHalf(int round, int halftime)
    {
        if (halftime <= 0 || round <= 1) return false;
        if (round == halftime + 1) return true;
        var regulation = halftime * 2;
        return round > regulation && (round - regulation - 1) % 3 == 0;
    }

    public static string ReasonLabel(string? reason) => reason switch
    {
        "elimination" => "Elimination",
        "bomb" => "Bomb exploded",
        "defuse" => "Bomb defused",
        "time" => "Time ran out",
        "surrender" => "Surrender",
        "draw" => "Draw",
        _ => "Round ended"
    };

    public static string ResultLabel(MatchStats match)
    {
        if (match.LocalTeam is not { } mine || match.OpposingTeam is not { } theirs) return "Match";
        return mine.Score > theirs.Score ? "Victory" : mine.Score < theirs.Score ? "Defeat" : "Draw";
    }

    public static string MapDisplayName(string? map)
    {
        if (string.IsNullOrWhiteSpace(map)) return "Unknown map";
        var name = map;
        foreach (var prefix in new[] { "de_", "cs_", "ar_", "gd_" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { name = name[prefix.Length..]; break; }
        name = name == "dust2" ? "Dust II" : name.Replace('_', ' ');
        return name.Length == 0 ? map : char.ToUpperInvariant(name[0]) + name[1..];
    }

    public static string ModeDisplayName(string? mode) => mode?.ToLowerInvariant() switch
    {
        "competitive" => "Competitive",
        "premier" => "Premier",
        "scrimcomp2v2" => "Wingman",
        "casual" => "Casual",
        null or "" => "Match",
        var other => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(other)
    };

    private static string Format(double? value, string format) => value?.ToString(format, CultureInfo.CurrentCulture) ?? "–";
}
