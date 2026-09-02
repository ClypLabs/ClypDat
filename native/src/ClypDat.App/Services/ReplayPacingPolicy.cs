namespace ClypDat.App.Services;

internal static class ReplayPacingPolicy
{
    internal const string Latest = "latest";
    internal const string Legacy = "legacy";
    private static readonly TimeSpan SchedulerLead = TimeSpan.FromMilliseconds(1);

    internal static bool IsLatest(string? value) => !string.Equals(value, Legacy, StringComparison.OrdinalIgnoreCase);

    // Consume one current deadline. Older elapsed deadlines become a timeline
    // jump, never a burst of stale encoder submissions.
    internal static long TakeLatestIntervals(TimeSpan now, TimeSpan interval, ref TimeSpan scheduledAt)
    {
        if (interval <= TimeSpan.Zero || now - scheduledAt + SchedulerLead < interval) return 0;
        var intervals = Math.Max(1L, (now - scheduledAt + SchedulerLead).Ticks / interval.Ticks);
        scheduledAt += TimeSpan.FromTicks(checked(interval.Ticks * intervals));
        return intervals;
    }
}
