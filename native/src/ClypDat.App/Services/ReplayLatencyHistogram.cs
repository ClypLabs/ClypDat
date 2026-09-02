namespace ClypDat.App.Services;

// Fixed one-millisecond buckets: recorder diagnostics need stable window
// percentiles, not per-frame allocations or a lock on the hot path.
internal sealed class ReplayLatencyHistogram
{
    private const int MaximumMilliseconds = 2048;
    private readonly long[] _buckets = new long[MaximumMilliseconds + 1];
    private long _count;
    private long _maximumMicroseconds;

    public void Record(TimeSpan elapsed)
    {
        var microseconds = Math.Max(0L, (long)Math.Round(elapsed.TotalMilliseconds * 1000));
        var bucket = (int)Math.Min(MaximumMilliseconds, microseconds / 1000);
        Interlocked.Increment(ref _buckets[bucket]);
        Interlocked.Increment(ref _count);
        while (true)
        {
            var old = Volatile.Read(ref _maximumMicroseconds);
            if (microseconds <= old || Interlocked.CompareExchange(ref _maximumMicroseconds, microseconds, old) == old) break;
        }
    }

    public (double P95Milliseconds, double MaximumMilliseconds) SnapshotAndReset()
    {
        var count = Interlocked.Exchange(ref _count, 0);
        var maximum = Interlocked.Exchange(ref _maximumMicroseconds, 0) / 1000.0;
        if (count == 0) return (0, maximum);
        var target = Math.Max(1L, (long)Math.Ceiling(count * .95));
        long seen = 0;
        for (var index = 0; index < _buckets.Length; index++)
        {
            seen += Interlocked.Exchange(ref _buckets[index], 0);
            if (seen >= target) return (index, maximum);
        }
        return (MaximumMilliseconds, maximum);
    }
}
