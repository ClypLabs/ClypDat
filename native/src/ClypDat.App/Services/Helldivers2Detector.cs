using System.Globalization;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

public sealed record Helldivers2FrameObservation(
    TimeSpan Timestamp,
    string CenterBannerText,
    string MissionPanelText,
    string KillCounterText,
    Helldivers2CounterVisibility CounterVisibility = Helldivers2CounterVisibility.Unknown,
    int? KillCounter = null);

public sealed record Helldivers2DetectedEvent(
    string EventId,
    string Label,
    TimeSpan Timestamp,
    string OccurrenceId,
    double Confidence,
    TimeSpan? StreakStart = null);

/// <summary>
/// Stateful event graph fed by text from normalized Helldivers 2 HUD regions.
/// Frame acquisition and OCR are adapters; live and offline paths share this
/// implementation so smoothing, thresholds, and reset behavior cannot drift.
/// </summary>
public sealed partial class Helldivers2Detector
{
    private static readonly int[] KillThresholds = [20, 50, 100];
    private readonly PhraseLatch _eliminated = new("ELIMINATED", confirmationFrames: 2, resetFrames: 6);
    private readonly PhraseLatch _successfulMission = new("SQUAD PAYOUT", confirmationFrames: 2, resetFrames: 20);
    private TimeSpan? _streakStart;
    private TimeSpan? _firstAbsent;
    private TimeSpan? _lastTimestamp;
    private int _absentSamples;
    private int _peak;
    private int? _pendingPeak;

    public IReadOnlyList<Helldivers2DetectedEvent> Observe(Helldivers2FrameObservation frame,
        IReadOnlySet<string>? enabledEvents = null)
    {
        // Duplicate or stale frames cannot confirm a peak or disappearance.
        if (_lastTimestamp is { } last && frame.Timestamp <= last) return [];
        _lastTimestamp = frame.Timestamp;
        var events = new List<Helldivers2DetectedEvent>(3);

        if (_eliminated.Observe(frame.CenterBannerText))
            events.Add(Create("eliminated", "Eliminated", frame.Timestamp, 0.98));

        if (_successfulMission.Observe(frame.MissionPanelText))
            events.Add(Create("successful-mission", "Successful Mission", frame.Timestamp, 0.98));

        if (frame.CounterVisibility != Helldivers2CounterVisibility.Absent)
        {
            _firstAbsent = null;
            _absentSamples = 0;
            if (frame.CounterVisibility == Helldivers2CounterVisibility.Present)
            {
                _streakStart ??= frame.Timestamp;
                var counter = frame.KillCounter;
                if (counter is null && TryParseKillCounter(frame.KillCounterText, out var parsed)) counter = parsed;
                if (counter is >= 0 and <= 999)
                {
                    // Confirm the earlier reading, never the new, unsupported high.
                    // A lower next reading replaces a spike; blanks preserve it.
                    if (_pendingPeak is { } pending && counter >= pending)
                        _peak = Math.Max(_peak, pending);
                    _pendingPeak = counter > _peak ? counter : null;
                }
            }
        }
        else if (_streakStart is { } start)
        {
            _firstAbsent ??= frame.Timestamp;
            if (++_absentSamples >= 3 && frame.Timestamp - _firstAbsent.Value >= TimeSpan.FromSeconds(1))
            {
                var threshold = KillThresholds.LastOrDefault(value => value <= _peak
                    && (enabledEvents is null || enabledEvents.Contains($"killstreak-{value}")));
                if (threshold > 0)
                    events.Add(Create($"killstreak-{threshold}", $"Killstreak ×{_peak}", _firstAbsent.Value, 0.95)
                        with { StreakStart = start });
                ResetStreak();
            }
        }

        return events;
    }

    public void ResetSession()
    {
        _eliminated.Reset();
        _successfulMission.Reset();
        ResetStreak();
        _lastTimestamp = null;
    }

    public static bool TryParseKillCounter(string? text, out int counter)
    {
        counter = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = KillCounterRegex().Match(text.ToUpperInvariant().Replace('×', 'X'));
        return match.Success
               && int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out counter)
               && counter is >= 0 and <= 999;
    }

    public void ObserveCaptureFailure()
    {
        _firstAbsent = null;
        _absentSamples = 0;
    }

    private void ResetStreak()
    {
        _streakStart = null;
        _peak = 0;
        _pendingPeak = null;
        ObserveCaptureFailure();
    }

    private static Helldivers2DetectedEvent Create(string id, string label, TimeSpan timestamp, double confidence) =>
        new(id, label, timestamp, $"{id}-{timestamp.TotalMilliseconds:F0}", confidence);

    [GeneratedRegex(@"^\s*(?:X\s*(?<count>\d{1,3})|(?<count>\d{1,3})\s*KILLS?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex KillCounterRegex();

}
