namespace ClypDat.App.Services;

public sealed record MatchReplaySegment(double StartSeconds, double EndSeconds, double ClipStartSeconds, double ClipEndSeconds)
{
    public bool Contains(double seconds) => seconds >= StartSeconds && seconds <= EndSeconds;
    public double ToClipSeconds(double seconds) => ClipStartSeconds +
        (seconds - StartSeconds) * (ClipEndSeconds - ClipStartSeconds) / (EndSeconds - StartSeconds);
}

/// <summary>Replay time represented by the saved media, including omitted capture intervals.</summary>
public sealed record MatchReplayWindow(double StartSeconds, double EndSeconds, double Rate)
{
    public IReadOnlyList<MatchReplaySegment>? Segments { get; init; }
    public bool Contains(double seconds) => double.IsFinite(seconds) &&
        (Segments is { Count: > 0 } segments ? segments.Any(segment => segment.Contains(seconds)) : seconds >= StartSeconds && seconds <= EndSeconds);
    public double ToClipSeconds(double replaySeconds) => Segments is { Count: > 0 } segments
        ? segments.FirstOrDefault(segment => segment.Contains(replaySeconds))?.ToClipSeconds(replaySeconds) ?? double.NaN
        : (replaySeconds - StartSeconds) / Rate;
}
