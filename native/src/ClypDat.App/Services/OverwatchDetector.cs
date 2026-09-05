using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

public sealed record OverwatchFrameObservation(
    TimeSpan Timestamp,
    string LeftColumnText,
    string KillFeedText,
    string TeamKillText);

public sealed record OverwatchDetectedEvent(
    string EventId,
    string Label,
    TimeSpan Timestamp,
    string OccurrenceId,
    double Confidence);

/// <summary>
/// Stateful event graph fed by text from normalized Overwatch HUD regions.
/// Same shape as <see cref="Helldivers2Detector"/>: frame acquisition and OCR
/// are adapters, so the live and offline paths cannot drift apart.
///
/// Overwatch prints each streak tier by name, so nothing is inferred from a
/// counter - the phrase IS the event. The one piece of real logic is
/// suppression: during a Play of the Game replay, and while you are watching
/// the player who killed you, the HUD on screen belongs to somebody else and
/// its kills are not yours.
/// </summary>
public sealed partial class OverwatchDetector
{
    // Ordered high tier first: a quintuple's own label is on screen alongside
    // nothing else, but the strip can still carry a stale lower tier mid-fade,
    // and the bigger streak is the one worth clipping.
    private static readonly (string Id, string Label, string Phrase)[] StreakTiers =
    [
        ("quintuple-kill", "Quintuple Kill", "QUINTUPLE KILL"),
        ("quadruple-kill", "Quadruple Kill", "QUADRUPLE KILL"),
        ("triple-kill", "Triple Kill", "TRIPLE KILL"),
        ("double-kill", "Double Kill", "DOUBLE KILL")
    ];

    private readonly Dictionary<string, PhraseLatch> _streaks = StreakTiers.ToDictionary(
        tier => tier.Id,
        _ => new PhraseLatch("", confirmationFrames: 1, resetFrames: 6),
        StringComparer.OrdinalIgnoreCase);

    private readonly PhraseLatch _teamKill = new("TEAM KILL", confirmationFrames: 1, resetFrames: 10);
    private readonly PhraseLatch _playOfTheGame = new("PLAY OF THE GAME", confirmationFrames: 2, resetFrames: 10);
    private readonly HashSet<string> _recentEliminations = new(StringComparer.OrdinalIgnoreCase);
    private Queue<string> _eliminationOrder = new();

    public OverwatchDetector()
    {
        foreach (var tier in StreakTiers) _streaks[tier.Id] = new PhraseLatch(tier.Phrase, 1, 6);
    }

    /// <summary>
    /// True while the frame is showing somebody else's game: a Play of the Game
    /// replay, or the death cam after you were eliminated. Kills read off the
    /// HUD in either state are not the local player's.
    /// </summary>
    public static bool IsSpectating(string leftColumnText) =>
        Contains(leftColumnText, "PLAY OF THE GAME")
        || Contains(leftColumnText, "ELIMINATED BY")
        || Contains(leftColumnText, "DEATH SPECTATING");

    public IReadOnlyList<OverwatchDetectedEvent> Observe(OverwatchFrameObservation frame)
    {
        var events = new List<OverwatchDetectedEvent>(2);

        // Play of the Game is the one event that fires while spectating -
        // it IS the spectated thing, and every POTG is worth keeping even when
        // the featured player is not you.
        if (_playOfTheGame.Observe(frame.LeftColumnText))
            events.Add(Create("play-of-the-game", "Play of the Game", frame.Timestamp, 0.97));

        if (IsSpectating(frame.LeftColumnText))
        {
            // Keep the latches fed so a streak that was on screen when the
            // replay started cannot fire the moment it ends.
            foreach (var tier in StreakTiers) _streaks[tier.Id].Observe(string.Empty);
            _teamKill.Observe(string.Empty);
            _recentEliminations.Clear();
            _eliminationOrder.Clear();
            return events;
        }

        foreach (var tier in StreakTiers)
        {
            if (_streaks[tier.Id].Observe(frame.KillFeedText))
                events.Add(Create(tier.Id, tier.Label, frame.Timestamp, 0.95));
        }

        if (_teamKill.Observe(frame.TeamKillText))
            events.Add(Create("team-kill", "Team Kill", frame.Timestamp, 0.96));

        foreach (var row in ParseEliminations(frame.KillFeedText))
        {
            if (!_recentEliminations.Add(row)) continue;
            _eliminationOrder.Enqueue(row);
            // The strip only ever shows a handful of rows; anything older than
            // that can legitimately recur as a fresh kill on the same player.
            while (_eliminationOrder.Count > 6) _recentEliminations.Remove(_eliminationOrder.Dequeue());
            events.Add(Create("elimination", "Elimination", frame.Timestamp, 0.90));
        }

        return events;
    }

    public void ResetSession()
    {
        foreach (var tier in StreakTiers) _streaks[tier.Id].Reset();
        _teamKill.Reset();
        _playOfTheGame.Reset();
        _recentEliminations.Clear();
        _eliminationOrder = new Queue<string>();
    }

    /// <summary>
    /// Elimination rows read "&lt;player&gt; &lt;damage&gt;" - the name of the enemy who
    /// died and how much the local player contributed to it, so the row means
    /// "you took part in this", assists included. "SAVED BY &lt;name&gt;" shares the
    /// strip and must never count.
    /// </summary>
    public static IReadOnlyList<string> ParseEliminations(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var rows = new List<string>();
        foreach (var line in text.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (Contains(trimmed, "SAVED BY")) continue;
            if (StreakTiers.Any(tier => Contains(trimmed, tier.Phrase))) continue;
            var match = EliminationRowRegex().Match(trimmed.ToUpperInvariant());
            if (match.Success) rows.Add($"{match.Groups["name"].Value} {match.Groups["damage"].Value}");
        }
        return rows;
    }

    private static bool Contains(string? text, string phrase) =>
        text?.Contains(phrase, StringComparison.OrdinalIgnoreCase) == true;

    private static OverwatchDetectedEvent Create(string id, string label, TimeSpan timestamp, double confidence) =>
        new(id, label, timestamp, $"{id}-{timestamp.TotalMilliseconds:F0}", confidence);

    [GeneratedRegex(@"^(?<name>[A-Z0-9._#\-]{2,24})\s+(?<damage>\d{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex EliminationRowRegex();
}
