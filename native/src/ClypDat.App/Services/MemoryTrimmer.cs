using System.Diagnostics;
using System.Runtime;

namespace ClypDat.App.Services;

// ClypDat spends most of its life as a background recorder with no window in
// front of the user, and it was sitting on ~800MB of working set while doing
// it. Most of that is not leaked - it is the editor's caches (extracted audio
// chunks at ~5.76MB each, decoded thumbnails) plus a managed heap the GC has
// no reason to shrink while nothing is pressuring it. Correct, and still the
// wrong thing to hold on a machine that is also running a game.
//
// So: once the editor is closed or recording stops, release data the app no
// longer owns and let the GC return it normally. Never evict active pages just
// to reduce Task Manager's working-set number.
public static class MemoryTrimmer
{
    // Set by MainWindow for diagnostics and possible future cleanup policy.
    public static volatile bool EditorOpen;

    // Whether a game is actually detected right now. This, not Recording, is
    // what decides whether the real (blocking, compacting) collection can run.
    //
    // Recording alone was too blunt: the buffer is armed for hours whether or
    // not anything is being played, and gating on it meant the full collection
    // effectively never ran - measured privateMb 1252 -> 1262 on an editor
    // close, i.e. nothing reclaimed, because the deferred background GC does
    // not compact the LOH and the LOH is where the 5.76MB audio chunks live.
    // The pause this avoids only matters when there is a game to drop frames
    // in; sitting on the desktop with the editor open, it costs nothing anyone
    // can see.
    public static volatile bool GameRunning;

    private static int _recording;
    // A compacting gen2 collection suspends managed threads. A transition out
    // of recording is the first safe point to request one, after the replay
    // ring has released its packets.
    public static bool Recording
    {
        get => Volatile.Read(ref _recording) != 0;
        set
        {
            var previous = Interlocked.Exchange(ref _recording, value ? 1 : 0);
            if (previous == 1 && !value) RequestTrim("recording stopped");
        }
    }

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    // Trimming is not free, so it is worth doing on a long idle stretch and not
    // worth repeating every half minute after that.
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(3);
    // Below this there is nothing meaningful to reclaim and the collection
    // would cost more than it returns. Measured on private bytes, not working
    // set: nothing evicts pages any more, so working set is no longer the
    // number that says whether the process is actually holding memory.
    private const long TrimThresholdBytes = 250L * 1024 * 1024;
    // IsReplayRecording flips on the view model; the replay ring returns its
    // packets in StopAsync. Those are not ordered against each other, and a
    // collection that runs while the ring is still rooted reclaims nothing.
    private static readonly TimeSpan StopSettleDelay = TimeSpan.FromSeconds(2);

    private static int _started;
    private static int _trimPending;
    private static DateTime _lastTrimUtc = DateTime.MinValue;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ClypDat.MemoryTrimmer",
            Priority = ThreadPriority.BelowNormal
        };
        thread.Start();
    }

    // The periodic pass is the only thing that reclaims anything in the app's
    // normal state, which is "recording, no window open, for hours". Dropping
    // it left the heap to grow until something else forced a collection.
    private static void Loop()
    {
        while (true)
        {
            Thread.Sleep(CheckInterval);
            try
            {
                if (DateTime.UtcNow - _lastTrimUtc < MinimumInterval) continue;

                using var process = Process.GetCurrentProcess();
                if (process.PrivateMemorySize64 < TrimThresholdBytes) continue;

                // The editor used to be skipped entirely, on the reasoning that
                // its caches are in use while it is open. But the editor is also
                // where the allocation actually happens - chunk buffers, decoded
                // frames, thumbnails - so skipping it meant the one state that
                // grows the process was the one state that never cleaned up, for
                // as long as it stayed open. The caches refill on demand; what
                // this drops is whatever is no longer being looked at.
                Trim(EditorOpen ? "idle (editor open)" : "idle");
            }
            catch
            {
                // Never let housekeeping take the app down.
            }
        }
    }

    // Also called directly at the points where a large, known-finished
    // allocation burst just ended (closing the editor, finishing a clip save),
    // rather than waiting out the idle timer for memory we already know is
    // dead.
    public static void Trim(string reason)
    {
        try
        {
            _lastTrimUtc = DateTime.UtcNow;
            long beforePrivate;
            using (var before = Process.GetCurrentProcess())
            {
                beforePrivate = before.PrivateMemorySize64;
            }

            var beforeManaged = GC.GetTotalMemory(false);
            // Only a game in the foreground buys the gentle path now.
            var deferred = Recording && GameRunning;

            AudioChunkCache.Clear();
            BitmapCache.Clear();

            if (deferred)
            {
                // No blocking, no compaction: a compacting gen2 suspends every
                // managed thread, and the capture and encode loops are managed
                // threads - a few hundred milliseconds there is dropped frames
                // in someone's game. This queues the work for the background
                // GC instead, which is what keeps the heap from growing without
                // bound across an all-day recording session.
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }
            else
            {
                // The audio chunks are 5.76MB byte[] each, so they live on the
                // large object heap, which a normal collection sweeps but never
                // compacts - without this the freed space stays as LOH holes
                // the process keeps reserved from the OS.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                // Second pass: the first one runs the finalizers for the bitmaps
                // dropped above, and an object only becomes collectable after
                // its finalizer has run.
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }

            using var settled = Process.GetCurrentProcess();
            // On the deferred path the collection is non-blocking, so it has not
            // run yet at this point - a before/after heap size there prints the
            // same number twice and reads as "the trim reclaimed nothing".
            var managed = deferred
                ? $"managedMb {beforeManaged / (1024 * 1024)} (collection deferred to the background GC - game running)"
                : $"managedMb {beforeManaged / (1024 * 1024)} -> {GC.GetTotalMemory(false) / (1024 * 1024)}";
            AppLog.Info(
                $"Memory cleanup ({reason}): privateMb {beforePrivate / (1024 * 1024)} -> {settled.PrivateMemorySize64 / (1024 * 1024)}, {managed}.");
        }
        catch (Exception error)
        {
            AppLog.Error($"Memory trim failed ({reason})", error);
        }
    }

    // Coalesced: replay can stop and restart several times in a few seconds
    // (game switch, quality change), and each of those transitions asking for
    // its own compacting gen2 is the stall this was meant to avoid. One pending
    // cleanup at a time, after the ring has had a moment to hand its packets
    // back.
    private static void RequestTrim(string reason)
    {
        if (Interlocked.Exchange(ref _trimPending, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StopSettleDelay).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _trimPending, 0);
            }

            // Recording came back before the delay elapsed - leave the heap
            // alone rather than stopping the capture threads we just restarted.
            if (Recording) return;
            Trim(reason);
        });
    }
}
