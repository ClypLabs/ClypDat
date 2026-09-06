using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

/// <summary>
/// A banner recognised by appearance rather than read as text. <see cref="Score"/>
/// is the correlation that recognised it, carried through to the detected event
/// so the log line reports a measurement rather than a constant.
/// </summary>
public sealed record DetectedBanner(string EventId, string Label, double Score);

public sealed record OverwatchFrameObservation(
    TimeSpan Timestamp,
    string LeftColumnText,
    string KillFeedText,
    string TeamKillText,
    IReadOnlyList<DetectedBanner>? BannerHits = null)
{
    public IReadOnlyList<DetectedBanner> Banners => BannerHits ?? Array.Empty<DetectedBanner>();
}

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
    // One latch per banner event, created as the templates report them, so the
    // detector needs no list of its own to keep in step with templates.json.
    private readonly Dictionary<string, PhraseLatch> _banners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Overwatch names the highlight both ways - the bar along the top of the
    /// replay reads "PLAY OF THE GAME" in one match and "PLAY OF THE MATCH" in
    /// the next - so both count, and both map to the same event.
    ///
    /// These are a fallback only. The bar is drawn in the same stylised display
    /// font as the streak banners, and Windows OCR cannot read it: it returns
    /// "PIWOfWfCßMf" for "PLAY OF THE GAME", measured on a tester's clip. The
    /// working path is the template match in templates.json; this catches the
    /// case where OCR happens to succeed on a spelling that is not templated.
    /// </summary>
    private static readonly string[] PlayOfTheGamePhrases = ["PLAY OF THE GAME", "PLAY OF THE MATCH"];

    private readonly PhraseLatch _playOfTheGame = new(PlayOfTheGamePhrases, confirmationFrames: 2, resetFrames: 10);
    private readonly HashSet<string> _recentEliminations = new(StringComparer.OrdinalIgnoreCase);
    private Queue<string> _eliminationOrder = new();

    /// <summary>
    /// True while the frame is showing somebody else's game: a Play of the Game
    /// replay, or the death cam after you were eliminated. Kills read off the
    /// HUD in either state are not the local player's.
    /// </summary>
    public static bool IsSpectating(string leftColumnText) =>
        PlayOfTheGamePhrases.Any(phrase => Contains(leftColumnText, phrase))
        || Contains(leftColumnText, "ELIMINATED BY")
        || Contains(leftColumnText, "DEATH SPECTATING");

    public IReadOnlyList<OverwatchDetectedEvent> Observe(OverwatchFrameObservation frame)
    {
        var events = new List<OverwatchDetectedEvent>(2);

        // The highlight bar is matched by appearance, not read: Windows OCR
        // returns "PIWOfWfCßMf" for it, the same way it mangles the streak
        // banners. Reading it as text is why a tester's Play of the Game was
        // saved as a Triple Kill - the replay went unrecognised, so the featured
        // player's banners were credited to them. The text phrases below stay as
        // a second chance at it, and cost nothing when OCR fails.
        var highlight = frame.Banners.FirstOrDefault(item => string.Equals(item.EventId, "play-of-the-game", StringComparison.OrdinalIgnoreCase));

        // Play of the Game is the one event that fires while spectating -
        // it IS the spectated thing, and every POTG is worth keeping even when
        // the featured player is not you.
        if (_playOfTheGame.ObservePresence(highlight is not null || PlayOfTheGamePhrases.Any(phrase => Contains(frame.LeftColumnText, phrase))))
            events.Add(Create("play-of-the-game", "Play of the Game", frame.Timestamp, highlight?.Score ?? 0.97));

        if (highlight is not null || IsSpectating(frame.LeftColumnText))
        {
            // Keep the latches fed so a streak that was on screen when the
            // replay started cannot fire the moment it ends.
            foreach (var latch in _banners.Values) latch.Observe(string.Empty);
            _recentEliminations.Clear();
            _eliminationOrder.Clear();
            return events;
        }

        // Banner events arrive already matched by appearance - see
        // GrayTemplateMatcher for why they cannot be read.
        foreach (var banner in frame.Banners)
        {
            if (!_banners.TryGetValue(banner.EventId, out var latch))
            {
                // Two frames, not one. Frames are sampled every 500ms and a
                // banner stays up ~3s, so a real one is seen six times over;
                // requiring two consecutive sightings costs nothing real and
                // drops the single-frame artefacts that survive the matcher.
                latch = new PhraseLatch(banner.EventId, confirmationFrames: 2, resetFrames: 8);
                _banners[banner.EventId] = latch;
            }
        }
        foreach (var (eventId, latch) in _banners)
        {
            var present = frame.Banners.FirstOrDefault(item => string.Equals(item.EventId, eventId, StringComparison.OrdinalIgnoreCase));
            if (latch.Observe(present is null ? string.Empty : eventId))
                events.Add(Create(eventId, present!.Label, frame.Timestamp, present.Score));
        }

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
        foreach (var latch in _banners.Values) latch.Reset();
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
            if (Contains(trimmed, "KILL")) continue;
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
