using System.Globalization;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

public sealed record Helldivers2FrameObservation(
    TimeSpan Timestamp,
    string CenterBannerText,
    string MissionPanelText,
    string KillCounterText);

public sealed record Helldivers2DetectedEvent(
    string EventId,
    string Label,
    TimeSpan Timestamp,
    string OccurrenceId,
    double Confidence);

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
    private readonly HashSet<int> _firedKillThresholds = [];
    private int _lastKillCounter;

    public IReadOnlyList<Helldivers2DetectedEvent> Observe(Helldivers2FrameObservation frame)
    {
        var events = new List<Helldivers2DetectedEvent>(3);
        var lifeReset = false;

        if (_eliminated.Observe(frame.CenterBannerText))
        {
            events.Add(Create("eliminated", "Eliminated", frame.Timestamp, 0.98));
            ResetLife();
            lifeReset = true;
        }

        if (_successfulMission.Observe(frame.MissionPanelText))
            events.Add(Create("successful-mission", "Successful Mission", frame.Timestamp, 0.98));

        if (!lifeReset && TryParseKillCounter(frame.KillCounterText, out var counter))
        {
            if (counter < _lastKillCounter) ResetLife();
            foreach (var threshold in KillThresholds)
            {
                if (_lastKillCounter < threshold && counter >= threshold && _firedKillThresholds.Add(threshold))
                    events.Add(Create($"killstreak-{threshold}", $"Killstreak ×{threshold}", frame.Timestamp, 0.95));
            }
            _lastKillCounter = counter;
        }

        return events;
    }

    public void ResetSession()
    {
        _eliminated.Reset();
        _successfulMission.Reset();
        ResetLife();
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

    private void ResetLife()
    {
        _lastKillCounter = 0;
        _firedKillThresholds.Clear();
    }

    private static Helldivers2DetectedEvent Create(string id, string label, TimeSpan timestamp, double confidence) =>
        new(id, label, timestamp, $"{id}-{timestamp.TotalMilliseconds:F0}", confidence);

    [GeneratedRegex(@"(?:X\s*(?<count>\d{1,3})|(?<count>\d{1,3})\s*KILLS?)", RegexOptions.CultureInvariant)]
    private static partial Regex KillCounterRegex();

    private sealed class PhraseLatch(string phrase, int confirmationFrames, int resetFrames)
    {
        private int _presentFrames;
        private int _absentFrames;
        private bool _latched;

        public bool Observe(string? text)
        {
            var present = text?.Contains(phrase, StringComparison.OrdinalIgnoreCase) == true;
            if (present)
            {
                _absentFrames = 0;
                _presentFrames++;
                if (!_latched && _presentFrames >= confirmationFrames)
                {
                    _latched = true;
                    return true;
                }
            }
            else
            {
                _presentFrames = 0;
                if (_latched && ++_absentFrames >= resetFrames) Reset();
            }
            return false;
        }

        public void Reset()
        {
            _presentFrames = 0;
            _absentFrames = 0;
            _latched = false;
        }
    }
}
