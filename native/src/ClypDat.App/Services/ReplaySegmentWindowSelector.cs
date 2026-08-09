namespace ClypDat.App.Services;

internal sealed record ReplaySegmentWindow(string Path, DateTime StartedAtUtc, DateTime EndedAtUtc, TimeSpan VideoDuration);

internal static class ReplaySegmentWindowSelector
{
    private static readonly TimeSpan MaximumStitchGap = TimeSpan.FromMilliseconds(500);

    internal static (ReplaySegmentWindow[] Segments, double FirstOffsetSeconds, double DurationSeconds) Select(
        IReadOnlyList<ReplaySegmentWindow> segments,
        DateTime requestedStartUtc,
        DateTime requestedEndUtc)
    {
        if (requestedEndUtc <= requestedStartUtc) return (Array.Empty<ReplaySegmentWindow>(), 0, 0);

        var candidates = segments
            .Where(segment => segment.EndedAtUtc > requestedStartUtc && segment.StartedAtUtc < requestedEndUtc)
            .OrderBy(segment => segment.StartedAtUtc)
            .ToArray();
        if (candidates.Length == 0) return (Array.Empty<ReplaySegmentWindow>(), 0, 0);

        var first = candidates.Length - 1;
        for (var index = candidates.Length - 1; index > 0; index--)
        {
            if (candidates[index].StartedAtUtc - candidates[index - 1].EndedAtUtc > MaximumStitchGap) break;
            first = index - 1;
        }

        var selected = candidates[first..];
        var effectiveStart = selected[0].StartedAtUtc < requestedStartUtc
            ? requestedStartUtc
            : selected[0].StartedAtUtc;
        var firstOffset = Math.Max(0, (effectiveStart - selected[0].StartedAtUtc).TotalSeconds);
        var availableDuration = Math.Max(0, selected.Sum(segment => segment.VideoDuration.TotalSeconds) - firstOffset);
        var requestedDuration = Math.Max(0, (requestedEndUtc - effectiveStart).TotalSeconds);
        return (selected, firstOffset, Math.Min(availableDuration, requestedDuration));
    }
}
