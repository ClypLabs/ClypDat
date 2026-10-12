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
//  Survival = 60% placement (1st = 100, 100th or worse = 0) + 40% recorded
//             time in match (0-20 minutes). These fixed references do not
//             pretend team placement is a count of players outlasted, or
//             treat the recording's end as the whole match's duration.
//  Building = unavailable: material totals are decoded, but a build-activity
//             score has not been verified across the current playlist and
//             replay formats. Left off entirely in Zero Build.
//  Looting  = unavailable: pickups are only in the network stream ClypDat
//             does not decode, and materials gathered alone is not looting.
public static class FortniteStatsPresentation
{
    internal static readonly (double Low, double High) EliminationsRange = (0, 10);
    internal static readonly (double Low, double High) DamageRange = (0, 3000);
    internal static readonly (double Low, double High) AccuracyRange = (15, 55);

    public const string BuildingUnavailable = "Building: ClypDat hasn't verified build activity for this replay format.";
    public const string LootingUnavailable = "Looting: Fortnite replays don't hold pickups in a form ClypDat can read.";
    public const string BuildModeUnknown = "Couldn't confirm whether this match used building.";

    public static double? CombatScore(MatchPlayer player) =>
        player.Damage is { } damage
            ? 100 * (0.5 * MatchStatsPresentation.Normalise(player.Kills, EliminationsRange) + 0.5 * MatchStatsPresentation.Normalise(damage, DamageRange))
            : null;

    public static double? AimScore(MatchPlayer player) =>
        player.AccuracyPercent is { } accuracy ? 100 * MatchStatsPresentation.Normalise(accuracy, AccuracyRange) : null;

    public static double? SurvivalScore(MatchStats match)
    {
        if (match.PlayerCount is not > 2 || match.Placement is not > 0 ||
            match.LocalEndSeconds is not { } end || !double.IsFinite(end) || end <= 0) return null;
        var placement = Math.Clamp((100 - match.Placement.Value) / 99.0, 0, 1);
        var lived = Math.Clamp(end / 1200, 0, 1);
        return 100 * (0.6 * placement + 0.4 * lived);
    }

    /// <summary>
    /// Five axes (Combat, Aim, Survival, Building, Looting), or four in a
    /// diamond for Zero Build: Combat top, Aim right, Survival bottom,
    /// Looting left. Only the playlist decides Zero Build.
    /// </summary>
    public static IReadOnlyList<MatchRadarAxis> RadarAxes(MatchStats match)
    {
        if (match.LocalPlayer is not { } player) return [];
        static MatchRadarAxis Axis(string label, double? score) => score is { } value
            ? new MatchRadarAxis(label, value / 100, value.ToString("0", CultureInfo.CurrentCulture))
            : MatchRadarAxis.Unavailable(label);
        var combat = Axis("Combat", CombatScore(player));
        var aim = Axis("Aim", AimScore(player));
        var survival = Axis("Survival", SurvivalScore(match));
        var looting = MatchRadarAxis.Unavailable("Looting");
        return match.ZeroBuild == true
            ? [combat, aim, survival, looting]
            : [combat, aim, survival, MatchRadarAxis.Unavailable("Building"), looting];
    }

    /// <summary>Why each unavailable axis is empty, one sentence each.</summary>
    public static IReadOnlyList<string> RadarNotes(MatchStats match)
    {
        var notes = new List<string>();
        if (match.ZeroBuild != true) notes.Add(BuildingUnavailable);
        notes.Add(LootingUnavailable);
        if (match.ZeroBuild is null) notes.Add(BuildModeUnknown);
        if (!match.LocalEventsAvailable) notes.Add("Your event totals are unavailable: the replay's player identity or some events couldn't be read.");
        if (match.ReplayDetailsIncomplete) notes.Add("Some replay data couldn't be read; recorded match history may be incomplete.");
        if (match.LocalPlayer is { } player)
        {
            if (CombatScore(player) is null) notes.Add("Combat: this replay has no end-of-match stats.");
            if (AimScore(player) is null) notes.Add("Aim: this replay has no accuracy.");
        }
        if (SurvivalScore(match) is null) notes.Add("Survival: placement needs a battle royale match with a recorded finish.");
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
