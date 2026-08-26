using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Media.Imaging;

namespace ClypDat.App.Services;

// Library cards used to decode their thumbnail from disk every time they
// scrolled into view and free it the moment they scrolled back out, so
// covering ground already scrolled paid the full read + Skia decode again. At
// wheel speed that is a decode storm queued behind a two-slot semaphore, and
// the cards that lost the race stayed blank while the scroller hitched.
// Cards borrow from here instead, so scrolling back is a dictionary lookup.
//
// This cache OWNS every bitmap it hands out: a card that scrolls away just
// drops its reference and never disposes. Eviction deliberately does NOT
// Dispose either - a card or the editor may still be bound to the bitmap, and
// freeing pixels under a live binding blanks it or crashes the compositor.
// Dropping the reference is enough; the finalizer releases the buffer once
// nothing draws it. Same reasoning as BitmapCache.
internal static class CardThumbnailCache
{
    // Thumbnails decode to card width (see
    // ClipCardViewModel.SetPreviewDecodeWidth), so a 480x270 BGRA card image
    // is ~500KB. 64MB holds well over a hundred of them - many viewports
    // worth, which is the whole point of the cache.
    private const long ByteBudget = 64L * 1024 * 1024;

    private sealed class Entry(Bitmap bitmap, long bytes, long lastUsedTicks)
    {
        public readonly Bitmap Bitmap = bitmap;
        public readonly long Bytes = bytes;
        public long LastUsedTicks = lastUsedTicks;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly object EvictionLock = new();
    private static long _residentBytes;

    // Decode width is part of the identity: resizing the window re-decodes at
    // the new card width, and the old size stays valid for whatever is still
    // bound to it until it ages out.
    private static string Key(string path, int width) => $"{width}|{path}";

    public static Bitmap? Get(string path, int width)
    {
        if (!Entries.TryGetValue(Key(path, width), out var entry)) return null;
        Volatile.Write(ref entry.LastUsedTicks, Stopwatch.GetTimestamp());
        return entry.Bitmap;
    }

    // Returns the instance that is actually cached, which may not be the one
    // passed in: two cards can race to decode the same path, and keeping the
    // first winner means every card shows one shared bitmap instead of paying
    // for a second copy of the same pixels.
    public static Bitmap Store(string path, int width, Bitmap bitmap)
    {
        var key = Key(path, width);
        if (Entries.TryGetValue(key, out var existing))
        {
            Volatile.Write(ref existing.LastUsedTicks, Stopwatch.GetTimestamp());
            if (!ReferenceEquals(existing.Bitmap, bitmap))
                DeferredBitmapDisposal.ReleaseReferenceAfterRender(bitmap);
            return existing.Bitmap;
        }

        var bytes = EstimateBytes(bitmap);
        if (!Entries.TryAdd(key, new Entry(bitmap, bytes, Stopwatch.GetTimestamp())))
        {
            // Lost the race between the lookup above and this add.
            if (Entries.TryGetValue(key, out var winner))
            {
                DeferredBitmapDisposal.ReleaseReferenceAfterRender(bitmap);
                return winner.Bitmap;
            }

            return bitmap;
        }

        Interlocked.Add(ref _residentBytes, bytes);
        EvictIfNeeded();
        return bitmap;
    }

    // Thumbnails are rewritten IN PLACE under the same path (moving a trim
    // handle regenerates {key}-v3.jpg), so a path drops every decode width it
    // is holding.
    public static void Invalidate(string path)
    {
        var suffix = $"|{path}";
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
        DeferredBitmapDisposal.ReleaseReferenceAfterRender(entry.Bitmap);
    }

    // Linear scan for the least-recently-used entry. Only runs when a new
    // entry actually pushes the resident set past the budget, and the budget
    // keeps the set small enough that the scan is noise next to a decode.
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

    private static long EstimateBytes(Bitmap bitmap)
    {
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
