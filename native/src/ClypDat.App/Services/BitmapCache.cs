using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Media.Imaging;

namespace ClypDat.App.Services;

// Thumbnail/filmstrip bitmaps are re-decoded from disk on every clip open with
// no cache at all - reopening the same clip seconds later paid a full disk
// read + JPEG decode again. Same shape as AudioChunkCache (ChunkedAudioReader.cs).
//
// Capped by decoded bytes, not entry count. The editor poster decodes at full
// 960x540 (~2MB BGRA), so the old 200-entry cap allowed ~400MB of pixels for
// what is really "the last few clips opened".
internal static class BitmapCache
{
    private const long ByteBudget = 48L * 1024 * 1024;

    private sealed class Entry(Bitmap? bitmap, long bytes, long lastUsedTicks)
    {
        public readonly Bitmap? Bitmap = bitmap;
        public readonly long Bytes = bytes;
        public long LastUsedTicks = lastUsedTicks;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly object EvictionLock = new();
    private static long _residentBytes;

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
        var entry = new Entry(bitmap, EstimateBytes(bitmap), Stopwatch.GetTimestamp());
        // AddOrUpdate's delegates can run more than once under contention, so
        // the byte accounting happens on the swap result instead.
        lock (EvictionLock)
        {
            if (Entries.TryGetValue(path, out var previous)) Retire(previous);
            Entries[path] = entry;
            Interlocked.Add(ref _residentBytes, entry.Bytes);
        }
        EvictIfNeeded();
    }

    // Entries are keyed by path alone, and several cache artifacts are
    // REWRITTEN in place under the same path - RegenerateThumbnailAsync
    // overwrites {key}-v3.jpg when a trim handle moves, and a trim save wipes
    // and rebuilds the whole cache entry. Without this the editor kept showing
    // the pre-trim image for the rest of the session while the library card -
    // which decodes straight from disk and never consults this cache - showed
    // the new one, so the two visibly disagreed.
    //
    // Deliberately does NOT Dispose the evicted bitmap: an Image control
    // somewhere may still be bound to it (the editor's own SelectedThumbnail is
    // the likely one at exactly this moment), and disposing out from under a
    // live binding blanks it or worse. Dropping the reference is enough - the
    // bitmap's own finalizer releases the unmanaged buffer.
    public static void Invalidate(string path)
    {
        lock (EvictionLock)
        {
            if (Entries.TryRemove(path, out var entry)) Retire(entry);
        }
    }

    // Drops every cached bitmap. Same deliberate non-disposal as Invalidate:
    // a library card or the editor may still be bound to one of these, and
    // disposing under a live binding blanks it. Releasing the reference is
    // what matters - the bitmap's finalizer frees the unmanaged pixel buffer
    // once nothing is drawing it.
    public static void Clear()
    {
        lock (EvictionLock)
        {
            foreach (var pair in Entries)
            {
                if (Entries.TryRemove(pair.Key, out var entry)) Retire(entry);
            }
        }
    }

    // Linear scan for the least-recently-used entry - the budget keeps the set
    // small, and it only runs when a new entry actually pushes the resident
    // set over it.
    private static void EvictIfNeeded()
    {
        lock (EvictionLock)
        {
            // One entry is always kept: the clip that was just opened needs its
            // poster even if it alone is over budget.
            while (Interlocked.Read(ref _residentBytes) > ByteBudget && Entries.Count > 1)
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
                if (Entries.TryRemove(oldestKey, out var entry)) Retire(entry);
            }
        }
    }

    private static void Retire(Entry entry)
    {
        Interlocked.Add(ref _residentBytes, -entry.Bytes);
        DeferredBitmapDisposal.ReleaseReferenceAfterRender(entry.Bitmap);
    }

    private static long EstimateBytes(Bitmap? bitmap)
    {
        if (bitmap is null) return 0;
        try
        {
            var size = bitmap.PixelSize;
            return Math.Max(1, (long)size.Width * size.Height * 4);
        }
        catch
        {
            return 1;
        }
    }
}
