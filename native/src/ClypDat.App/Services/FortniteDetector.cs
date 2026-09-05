using System.Globalization;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

public sealed record FortniteFrameObservation(
    TimeSpan Timestamp,
    string KillFeedText,
    string BannerText,
    string UpperCentreText);

public sealed record FortniteDetectedEvent(
    string EventId,
    string Label,
    TimeSpan Timestamp,
    string OccurrenceId,
    double Confidence);

/// <summary>
/// Stateful event graph fed by text from normalized Fortnite HUD regions.
///
/// Fortnite's kill feed is full sentences rather than labels, which makes it
/// the richest source of the three games: one region yields the elimination,
/// who made it, and the distance. The banner supplies the streak tiers, which
/// the feed does not name.
/// </summary>
public sealed partial class FortniteDetector
{
    private readonly PhraseLatch _victoryRoyale = new("VICTORY ROYALE", confirmationFrames: 2, resetFrames: 20);
    private readonly PhraseLatch _eliminatedBy = new("ELIMINATED BY", confirmationFrames: 2, resetFrames: 20);
    private readonly PhraseLatch _doubleElimination = new("DOUBLE ELIM", confirmationFrames: 1, resetFrames: 8);
    private readonly PhraseLatch _enemyTeamWiped = new("ENEMY TEAM WIPED", confirmationFrames: 1, resetFrames: 12);
    private readonly MultiEliminationLatch _multiElimination = new();
    private readonly HashSet<string> _recentFeedLines = new(StringComparer.Ordinal);
    private Queue<string> _feedOrder = new();
    private string? _localPlayer;

    /// <summary>
    /// The display name read from Fortnite's log. Null means ownership cannot be
    /// established, and feed events are suppressed rather than attributed to a
    /// player who may not be you.
    /// </summary>
    public void SetLocalPlayer(string? displayName) => _localPlayer = displayName;

    public IReadOnlyList<FortniteDetectedEvent> Observe(FortniteFrameObservation frame)
    {
        var events = new List<FortniteDetectedEvent>(3);

        if (_victoryRoyale.Observe(frame.UpperCentreText))
            events.Add(Create("victory-royale", "Victory Royale", frame.Timestamp, 0.97));
        if (_eliminatedBy.Observe(frame.UpperCentreText))
            events.Add(Create("got-eliminated", "Got Eliminated", frame.Timestamp, 0.96));

        // Ordered biggest first so a frame carrying both a stale double and a
        // fresh triple reports the triple.
        if (_enemyTeamWiped.Observe(frame.BannerText))
            events.Add(Create("enemy-team-wiped", "Enemy Team Wiped", frame.Timestamp, 0.95));
        if (_multiElimination.Observe(frame.BannerText, out var multiLabel))
            events.Add(Create("multi-elimination", multiLabel, frame.Timestamp, 0.95));
        if (_doubleElimination.Observe(frame.BannerText))
            events.Add(Create("double-elimination", "Double Elimination", frame.Timestamp, 0.95));

        foreach (var kill in ParseOwnEliminations(frame.KillFeedText, _localPlayer))
        {
            // The same line sits in the feed for several sampled frames.
            if (!_recentFeedLines.Add(kill.Line)) continue;
            _feedOrder.Enqueue(kill.Line);
            while (_feedOrder.Count > 8) _recentFeedLines.Remove(_feedOrder.Dequeue());

            events.Add(Create("eliminated-player", "Eliminated Player", frame.Timestamp, 0.92));
            // Fortnite only prints the distance on kills far enough away to be
            // worth mentioning, so the suffix existing IS the event - no
            // threshold of our own to pick.
            if (kill.Metres is { } metres)
                events.Add(Create("distance-shot", $"Distance Shot ({metres} M)", frame.Timestamp, 0.92));
        }

        return events;
    }

    public void ResetSession()
    {
        _victoryRoyale.Reset();
        _eliminatedBy.Reset();
        _doubleElimination.Reset();
        _enemyTeamWiped.Reset();
        _multiElimination.Reset();
        _recentFeedLines.Clear();
        _feedOrder = new Queue<string>();
    }

    public readonly record struct FeedKill(string Line, int? Metres);

    /// <summary>
    /// Feed lines read
    /// "&lt;killer&gt; (&lt;level&gt;) eliminated &lt;victim&gt; (&lt;level&gt;) with a &lt;weapon&gt; (&lt;N&gt; m)",
    /// with the distance present only on long-range kills. "knocked out" is a
    /// knock rather than an elimination and is deliberately ignored.
    ///
    /// Only lines whose killer is the local player count: the feed carries
    /// everybody's kills, and in the capture the local name is distinguished
    /// only by being green, which greyscale OCR cannot see.
    /// </summary>
    public static IReadOnlyList<FeedKill> ParseOwnEliminations(string? text, string? localPlayer)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(localPlayer)) return Array.Empty<FeedKill>();
        var kills = new List<FeedKill>();
        foreach (var line in text.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var match = EliminationLineRegex().Match(trimmed);
            if (!match.Success) continue;
            if (!FortniteIdentity.IsLocalPlayer(match.Groups["killer"].Value, localPlayer)) continue;
            int? metres = match.Groups["metres"].Success
                && int.TryParse(match.Groups["metres"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
            kills.Add(new FeedKill(trimmed, metres));
        }
        return kills;
    }

    private static FortniteDetectedEvent Create(string id, string label, TimeSpan timestamp, double confidence) =>
        new(id, label, timestamp, $"{id}-{timestamp.TotalMilliseconds:F0}", confidence);

    // The killer name runs up to the level in brackets or the verb itself, so
    // it tolerates spaces and the decorative characters players put in names.
    [GeneratedRegex(@"^(?<killer>.+?)\s*(?:\(\d+\)\s*)?eliminated\s+(?<victim>.+?)\s*(?:\(\d+\)\s*)?with\b.*?(?:\((?<metres>\d{1,4})\s*m\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EliminationLineRegex();

    /// <summary>
    /// Triple and above all map to the catalog's single Multi-Elimination event,
    /// but the tile keeps the tier the game actually printed. Only "TRIPLE ELIM"
    /// is confirmed from capture; the higher tiers follow the same pattern and
    /// are matched defensively.
    /// </summary>
    private sealed class MultiEliminationLatch
    {
        private static readonly (string Phrase, string Label)[] Tiers =
        [
            ("SQUAD WIPE", "Squad Wipe"),
            ("PENTA ELIM", "Penta Elimination"),
            ("QUADRA ELIM", "Quadra Elimination"),
            ("QUAD ELIM", "Quad Elimination"),
            ("TRIPLE ELIM", "Triple Elimination")
        ];

        private readonly Dictionary<string, PhraseLatch> _latches =
            Tiers.ToDictionary(tier => tier.Phrase, tier => new PhraseLatch(tier.Phrase, 1, 8), StringComparer.Ordinal);

        public bool Observe(string? text, out string label)
        {
            label = "Multi-Elimination";
            var fired = false;
            foreach (var tier in Tiers)
            {
                if (!_latches[tier.Phrase].Observe(text)) continue;
                if (fired) continue;
                fired = true;
                label = tier.Label;
            }
            return fired;
        }

        public void Reset()
        {
            foreach (var latch in _latches.Values) latch.Reset();
        }
    }
}
