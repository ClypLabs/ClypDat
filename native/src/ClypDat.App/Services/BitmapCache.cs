using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Media.Imaging;

namespace ClypDat.App.Services;

// Thumbnail/filmstrip bitmaps are re-decoded from disk on every clip open with
// no cache at all - reopening the same clip seconds later paid a full disk
// read + JPEG decode again. Same shape as AudioChunkCache (ChunkedAudioReader.cs)
// but capped by entry count, not bytes - these images are small and few enough
// that a fixed cap is simpler to reason about than tracking their pixel size.
internal static class BitmapCache
{
    private const int Capacity = 200;

    private sealed class Entry(Bitmap? bitmap, long lastUsedTicks)
    {
        public readonly Bitmap? Bitmap = bitmap;
        public long LastUsedTicks = lastUsedTicks;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly object EvictionLock = new();

    public static bool TryGet(string path, out Bitmap? bitmap)
    {
        if (Entries.TryGetValue(path, out var entry))
        {
            Volatile.Write(ref entry.LastUsedTicks, Stopwatch.GetTimestamp());
            bitmap = entry.Bitmap;
            return true;
        }

        bitmap = null;
        return false;
    }

    public static void Store(string path, Bitmap? bitmap)
    {
        Entries[path] = new Entry(bitmap, Stopwatch.GetTimestamp());
        EvictIfNeeded();
    }

    // Linear scan for the least-recently-used entry - the capacity cap keeps
    // this cheap, and it only runs when a new entry actually pushes the
    // resident set over the cap.
    private static void EvictIfNeeded()
    {
        lock (EvictionLock)
        {
            while (Entries.Count > Capacity)
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
                Entries.TryRemove(oldestKey, out _);
            }
        }
    }
}
