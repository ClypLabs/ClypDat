using System.Globalization;

namespace ClypDat.App.Services;

public sealed record MatchTimelineRow(double MatchSeconds, string Time, string Title, string Detail, string Kind, bool InClip, string? ClipTime);

// What the Stats panel shows for a Fortnite match, kept free of Avalonia so
// the maths can be tested.
//
// Radar scores are ClypDat's own, 0-100, against fixed reference ranges so a
// shape means the same thing from one match to the next (Ascent's formulas
// are not public). An axis whose data the replay does not hold is returned
// unavailable, never as a 0:
//  Combat   = 50% eliminations (0-10) + 50% damage to players (0-3000).
//  Aim      = accuracy, 15% (poor) to 55% (excellent). Replays carry no
//             headshot data, so it is accuracy alone.
//  Survival = 100 for a recorded win. Otherwise the fraction of other
//             players outlasted: (starting players - players remaining at
//             your finish) / (starting players - 1), multiplied by 100.
//             Remaining players includes you. Counts must be recorded and
//             consistent; match duration and team placement are not proxies.
//  Building = unavailable: material totals are decoded, but a build-activity
//             score has not been verified across the current playlist and
//             replay formats. Left off entirely in Zero Build.
//  Looting  = collected pickups (0-100), excluding your own dropped items.
//             Counts each item stack once, rather than ammunition quantity.
public static class FortniteStatsPresentation
{
    internal static readonly (double Low, double High) EliminationsRange = (0, 10);
    internal static readonly (double Low, double High) DamageRange = (0, 3000);
    internal static readonly (double Low, double High) AccuracyRange = (15, 55);

    public const string BuildingUnavailable = "Building: ClypDat hasn't verified build activity for this replay format.";
    public const string LootingUnavailable = "Looting: this replay's pickups couldn't be fully read or attributed to you.";
    public const string LootingPartial = "Looting counts confirmed pickups; some collection events have no player identity.";
    public const string BuildModeUnknown = "Couldn't confirm whether this match used building.";
    public const string SurvivalUnavailable = "Survival: a recorded win or valid starting and remaining player counts are needed.";

    public static double? CombatScore(MatchPlayer player) =>
        player.Damage is { } damage
            ? 100 * (0.5 * MatchStatsPresentation.Normalise(player.Kills, EliminationsRange) + 0.5 * MatchStatsPresentation.Normalise(damage, DamageRange))
            : null;

    public static double? AimScore(MatchPlayer player) =>
        player.AccuracyPercent is { } accuracy ? 100 * MatchStatsPresentation.Normalise(accuracy, AccuracyRange) : null;

    public static double? LootingScore(MatchPlayer player) =>
        player.LootingPickups is >= 0 and var pickups ? Math.Clamp(pickups, 0, 100) : null;

    public static double? SurvivalScore(MatchStats match)
    {
        if (match.Won) return 100;
        if (match.PlayerCount is not { } starting || starting <= 1 ||
            match.PlayersLeftAtEnd is not { } remaining || remaining < 1 || remaining > starting) return null;
        return 100d * (starting - remaining) / (starting - 1);
    }

    private static string SurvivalExplanation(MatchStats match) => match.Won
        ? "Won the match. A victory earns full Survival points, regardless of match length."
        : SurvivalScore(match) is { } score
            ? $"Recorded lobby: {match.PlayerCount} players.\n{match.PlayersLeftAtEnd} players remained at your finish, including you.\nYou outlasted {match.PlayerCount - match.PlayersLeftAtEnd} of {match.PlayerCount - 1} other players: {score:0.#}/100."
            : "Starting or remaining player counts are missing or inconsistent, so Survival is unavailable.";

    /// <summary>
    /// Five axes (Combat, Aim, Survival, Building, Looting), or four in a
    /// diamond for Zero Build: Survival top, Combat right, Looting bottom,
    /// Aim left. Building takes a lower spoke when present.
    /// </summary>
    public static IReadOnlyList<MatchRadarAxis> RadarAxes(MatchStats match)
    {
        if (match.LocalPlayer is not { } player) return [];
        static MatchRadarAxis Axis(string label, double? score, string explanation) => (score is { } value
            ? new MatchRadarAxis(label, value / 100, value.ToString("0", CultureInfo.CurrentCulture))
            : MatchRadarAxis.Unavailable(label)) with { Explanation = explanation };
        var combat = Axis("Combat", CombatScore(player), player.Damage is { } damage
            ? $"{player.Kills} eliminations contributed {50 * MatchStatsPresentation.Normalise(player.Kills, EliminationsRange):0.#}/50 points.\n{damage:N0} damage to players contributed {50 * MatchStatsPresentation.Normalise(damage, DamageRange):0.#}/50 points.\nFull points at 10 eliminations and 3,000 damage."
            : "Player damage wasn't recorded for this match, so no Combat score can be calculated.");
        var aim = Axis("Aim", AimScore(player), player.AccuracyPercent is { } accuracy
            ? $"{accuracy:0.#}% accuracy.\nAccuracy of 15% or less scores 0; 55% or more scores 100."
            : "Accuracy wasn't recorded for this match, so no Aim score can be calculated.");
        var survival = Axis("Survival", SurvivalScore(match), SurvivalExplanation(match));
        var looting = Axis("Looting", LootingScore(player), player.LootingPickups is >= 0 and var pickups
            ? $"{pickups} confirmed pickups.\nEach collected item stack adds 1 point, up to 100. Picking up your own dropped items adds no points."
                + (player.LootingPartial ? "\nSome pickups couldn't be attributed to you; this score is a confirmed minimum." : string.Empty)
            : "Your collected pickups couldn't be confirmed for this match, so no Looting score can be calculated.");
        if (player.LootingPartial && LootingScore(player) is { } recorded)
            looting = looting with { Display = $"≥{recorded:0}" };
        survival = survival with { AngleDegrees = -90 };
        combat = combat with { AngleDegrees = 0 };
        looting = looting with { AngleDegrees = 90 };
        aim = aim with { AngleDegrees = 180 };
        return match.ZeroBuild == true
            ? [survival, combat, looting, aim]
            : [survival, combat, MatchRadarAxis.Unavailable("Building") with
                { AngleDegrees = 45, Explanation = "No verified building activity for this match. Material totals aren't used to calculate a Building score." }, looting, aim];
    }

    /// <summary>Why each unavailable axis is empty, one sentence each.</summary>
    public static IReadOnlyList<string> RadarNotes(MatchStats match)
    {
        var notes = new List<string>();
        if (match.ZeroBuild != true) notes.Add(BuildingUnavailable);
        if (match.LocalPlayer is not { } local || LootingScore(local) is null) notes.Add(LootingUnavailable);
        else if (local.LootingPartial) notes.Add(LootingPartial);
        if (match.ZeroBuild is null) notes.Add(BuildModeUnknown);
        if (!match.LocalEventsAvailable) notes.Add("Your event totals are unavailable: the replay's player identity or some events couldn't be read.");
        if (match.ReplayDetailsIncomplete) notes.Add("Some replay data couldn't be read; recorded match history may be incomplete.");
        if (match.LocalPlayer is { } player)
        {
            if (CombatScore(player) is null) notes.Add("Combat: this replay has no end-of-match stats.");
            if (AimScore(player) is null) notes.Add("Aim: this replay has no accuracy.");
        }
        if (SurvivalScore(match) is null) notes.Add(SurvivalUnavailable);
        return notes;
    }

    public static IReadOnlyList<MatchStatTile> Tiles(MatchStats match)
    {
        var player = match.LocalPlayer;
        return
        [
            new("Place", match.Placement is { } place ? $"#{place}" : Dash),
            new("Elims", match.HasMatchSummary ? player?.Kills.ToString(CultureInfo.CurrentCulture) ?? Dash : Dash),
            new("Damage", player?.Damage?.ToString("N0", CultureInfo.CurrentCulture) ?? Dash),
            new("Accuracy", player?.AccuracyPercent is { } accuracy ? $"{accuracy:0}%" : Dash)
        ];
    }

    /// <summary>
    /// The clip's own tiles: what can be counted from events inside it.
    /// Damage and accuracy only exist as match totals, so they read "—".
    /// </summary>
    public static IReadOnlyList<MatchStatTile> ClipTiles(MatchStats match, MatchReplayWindow window)
    {
        var inside = match.Events.Where(e => e.DemoSeconds is { } s && window.Contains(s)).ToList();
        return
        [
            new("Elims", match.LocalEventsAvailable ? inside.Count(e => e.Kind == "kill").ToString(CultureInfo.CurrentCulture) : Dash),
            new("Knocks", match.LocalEventsAvailable ? inside.Count(e => e.Kind == "knock").ToString(CultureInfo.CurrentCulture) : Dash),
            new("Damage", Dash),
            new("Accuracy", Dash)
        ];
    }

    public static IReadOnlyList<MatchTimelineRow> Timeline(MatchStats match, MatchReplayWindow? window)
    {
        var rows = new List<MatchTimelineRow>();
        foreach (var e in match.Events)
        {
            if (e.DemoSeconds is not { } seconds) continue;
            var (title, detail) = e.Kind switch
            {
                "kill" => ("Elimination", e.Bot ? "Bot" : string.Empty),
                "knock" => ("Knock", e.Bot ? "Bot" : string.Empty),
                "knocked" => ("Knocked down", string.Empty),
                "death" when e.Final => ("Eliminated", match.Placement is { } place ? $"#{place}" : string.Empty),
                "death" => ("Eliminated", string.Empty),
                "victory" => ("Victory Royale", match.Placement is 1 ? "#1" : string.Empty),
                _ => (string.Empty, string.Empty)
            };
            if (title.Length == 0) continue;
            var inClip = window?.Contains(seconds) == true;
            rows.Add(new MatchTimelineRow(seconds, Clock(seconds), title, detail, e.Kind, inClip,
                inClip ? Clock(window!.ToClipSeconds(seconds)) : null));
        }
        return rows;
    }

    public static string PlacementSummary(MatchStats match)
    {
        if (match.PlayersLeft.Count == 0) return string.Empty;
        var total = match.PlayersLeft[0].Count;
        if (match.Won) return $"Players left {total} → Victory Royale";
        return match.PlayersLeftAtEnd is { } left ? $"Players left {total} → {left}" : string.Empty;
    }

    public static IReadOnlyList<(string Label, string Value)> MatchDetails(MatchStats match)
    {
        var rows = new List<(string, string)>();
        if (match.LocalEndSeconds is { } end) rows.Add(("Time in match", Clock(end)));
        if (match.LocalPlayer is not { } player) return rows;
        if (player.Knocks is { } knocks) rows.Add(("Knocks", knocks.ToString(CultureInfo.CurrentCulture)));
        if (match.HasMatchSummary) rows.Add(("Assists", player.Assists.ToString(CultureInfo.CurrentCulture)));
        if (player.DamageTaken is { } taken) rows.Add(("Damage taken", taken.ToString("N0", CultureInfo.CurrentCulture)));
        if (player.StructureDamage is { } structures) rows.Add(("Damage to structures", structures.ToString("N0", CultureInfo.CurrentCulture)));
        if (player.Revives is { } revives) rows.Add(("Revives", revives.ToString(CultureInfo.CurrentCulture)));
        if (player.LootingPickups is >= 0 and var pickups) rows.Add(("Items picked up", (player.LootingPartial ? "≥" : string.Empty) + pickups.ToString(CultureInfo.CurrentCulture)));
        if (player.DistanceMetres is { } metres)
            rows.Add(("Distance travelled", metres >= 1000 ? $"{metres / 1000:0.0} km" : $"{metres:0} m"));
        return rows;
    }

    /// <summary>m:ss, or h:mm:ss past an hour.</summary>
    public static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    private const string Dash = "—";
}
