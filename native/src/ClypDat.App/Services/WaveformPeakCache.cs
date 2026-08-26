using System.Collections.Concurrent;
using System.Diagnostics;

namespace ClypDat.App.Services;

// Editor waveform peaks, held in memory so opening a clip can paint them
// SYNCHRONOUSLY while the timeline lanes are being built, before the editor is
// ever shown. The disk cache underneath ({key}-waveforms-v2.json) already made
// a reopen cheap, but it is still a file read on a background thread followed
// by a dispatcher hop - which means at least one painted frame where the lane
// is an empty box, then the waveform appearing. This cache is what removes that
// frame.
//
// Unlike BitmapCache and CardThumbnailCache there is NO disposal hazard here:
// these are plain double[]. Eviction is a reference drop, and a lane still
// bound to an evicted array keeps drawing correctly forever.
//
// The arrays handed out are SHARED across every lane and every reopen and must
// never be mutated in place. Only finished, immutable results are stored - in
// particular the segmented decode path mutates its working arrays while it
// publishes copies, and only its final dictionary reaches this cache.
internal static class WaveformPeakCache
{
    // 700 buckets x ~5 audio tracks x 8 bytes is ~28KB per clip, so 8MB holds
    // roughly 280 clips - far more than a session will ever open, and far more
    // than the idle warm sweep is allowed to put in it.
    private const long ByteBudget = 8L * 1024 * 1024;

    private sealed class Entry(IReadOnlyDictionary<int, IReadOnlyList<double>> peaks, long bytes, long lastUsedTicks)
    {
        public readonly IReadOnlyDictionary<int, IReadOnlyList<double>> Peaks = peaks;
        public readonly long Bytes = bytes;
        public long LastUsedTicks = lastUsedTicks;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly object EvictionLock = new();
    private static long _residentBytes;

    // Size and mtime are IN the key rather than validated against it. A
    // trim-save rewrites the clip at the SAME path, and a path-only key would
    // keep serving the pre-trim waveform for the rest of the session - the
    // exact bug the on-disk cache had.
    private static string Key(string path, long sizeBytes, DateTime lastWriteUtc) =>
        $"{sizeBytes}|{lastWriteUtc.Ticks}|{path.ToLowerInvariant()}";

    public static IReadOnlyDictionary<int, IReadOnlyList<double>>? Get(string path, long sizeBytes, DateTime lastWriteUtc)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!Entries.TryGetValue(Key(path, sizeBytes, lastWriteUtc), out var entry)) return null;
        Volatile.Write(ref entry.LastUsedTicks, Stopwatch.GetTimestamp());
        return entry.Peaks;
    }

    public static void Store(string path, long sizeBytes, DateTime lastWriteUtc, IReadOnlyDictionary<int, IReadOnlyList<double>> peaks)
    {
        if (string.IsNullOrEmpty(path) || peaks.Count == 0) return;

        var key = Key(path, sizeBytes, lastWriteUtc);
        var bytes = peaks.Sum(pair => (long)pair.Value.Count * sizeof(double) + 64);
        var entry = new Entry(peaks, bytes, Stopwatch.GetTimestamp());
        if (Entries.TryGetValue(key, out var existing))
        {
            // Same clip decoded twice (an editor open racing the warm sweep).
            // Keep the resident one so every lane keeps sharing one instance -
            // TimelineLaneControl caches its geometry on ReferenceEquals, so
            // swapping in an equal-but-different array would retessellate
            // ~1400 segments per lane to draw the identical shape.
            Volatile.Write(ref existing.LastUsedTicks, Stopwatch.GetTimestamp());
            return;
        }

        if (!Entries.TryAdd(key, entry)) return;
        Interlocked.Add(ref _residentBytes, bytes);
        EvictIfNeeded();
    }

    // Belt and braces next to the size+mtime key: a rewritten file cannot
    // collide with its own old entry, but this frees the bytes immediately and
    // covers the pathological same-size-same-mtime rewrite.
    public static void Invalidate(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var suffix = "|" + path.ToLowerInvariant();
        foreach (var key in Entries.Keys)
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal)) Remove(key);
        }
    }

    public static void Clear()
    {
        foreach (var key in Entries.Keys) Remove(key);
    }

    private static void Remove(string key)
    {
        if (!Entries.TryRemove(key, out var entry)) return;
        Interlocked.Add(ref _residentBytes, -entry.Bytes);
    }

    private static void EvictIfNeeded()
    {
        lock (EvictionLock)
        {
            while (Interlocked.Read(ref _residentBytes) > ByteBudget)
            {
                string? oldestKey = null;
                var oldestTicks = long.MaxValue;
                foreach (var pair in Entries)
                {
                    var ticks = Volatile.Read(ref pair.Value.LastUsedTicks);
                    if (ticks < oldestTicks)
                    {
                        oldestTicks = ticks;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey is null) return;
                Remove(oldestKey);
            }
        }
    }
}
