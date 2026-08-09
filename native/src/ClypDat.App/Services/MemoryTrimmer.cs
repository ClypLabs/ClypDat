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
            if (previous == 1 && !value) RequestStoppedRecordingTrim();
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
    private static readonly AutoResetEvent TrimRequested = new(false);
    private static readonly object RequestGate = new();
    private static DateTime _lastTrimUtc = DateTime.MinValue;
    private static string? _pendingReason;
    private static int _coalescedRequests;

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
        var nextPeriodicCheckUtc = DateTime.UtcNow + CheckInterval;
        while (true)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now >= nextPeriodicCheckUtc)
                {
                    nextPeriodicCheckUtc = now + CheckInterval;
                    using var process = Process.GetCurrentProcess();
                    if (process.PrivateMemorySize64 >= TrimThresholdBytes)
                    {
                        RequestTrim(EditorOpen ? "idle (editor open)" : "idle");
                    }
                }

                if (TryTakeRequest(now, out var reason, out var coalesced))
                {
                    RunTrim(reason, coalesced);
                    continue;
                }

                var wakeAt = nextPeriodicCheckUtc;
                if (HasPendingRequest())
                {
                    var nextTrimUtc = _lastTrimUtc + MinimumInterval;
                    if (nextTrimUtc < wakeAt) wakeAt = nextTrimUtc;
                }
                var wait = wakeAt - DateTime.UtcNow;
                TrimRequested.WaitOne(wait <= TimeSpan.Zero ? TimeSpan.Zero : wait);
            }
            catch (Exception error)
            {
                AppLog.Error("Memory trim scheduler failed", error);
                TrimRequested.WaitOne(CheckInterval);
            }
        }
    }

    // Also called directly at the points where a large, known-finished
    // allocation burst just ended (closing the editor, finishing a clip save),
    // rather than waiting out the idle timer for memory we already know is
    // dead.
    public static void RequestTrim(string reason)
    {
        lock (RequestGate)
        {
            if (_pendingReason is not null) _coalescedRequests++;
            _pendingReason = reason;
        }

        TrimRequested.Set();
    }

    private static bool TryTakeRequest(DateTime now, out string reason, out int coalesced)
    {
        reason = string.Empty;
        coalesced = 0;
        if (now - _lastTrimUtc < MinimumInterval) return false;

        lock (RequestGate)
        {
            if (_pendingReason is null) return false;
            reason = _pendingReason;
            coalesced = _coalescedRequests;
            _pendingReason = null;
            _coalescedRequests = 0;
            return true;
        }
    }

    private static bool HasPendingRequest()
    {
        lock (RequestGate) return _pendingReason is not null;
    }

    private static void RunTrim(string reason, int coalesced)
    {
        try
        {
            _lastTrimUtc = DateTime.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            long beforePrivate;
            using (var before = Process.GetCurrentProcess())
            {
                beforePrivate = before.PrivateMemorySize64;
            }

            var beforeManaged = GC.GetTotalMemory(false);
            var beforeGen2 = GC.CollectionCount(2);
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
            }

            using var settled = Process.GetCurrentProcess();
            // On the deferred path the collection is non-blocking, so it has not
            // run yet at this point - a before/after heap size there prints the
            // same number twice and reads as "the trim reclaimed nothing".
            var managed = deferred
                ? $"managedMb {beforeManaged / (1024 * 1024)} (collection deferred to the background GC - game running)"
                : $"managedMb {beforeManaged / (1024 * 1024)} -> {GC.GetTotalMemory(false) / (1024 * 1024)}";
            AppLog.Info(
                $"Memory cleanup ({reason}): privateMb {beforePrivate / (1024 * 1024)} -> {settled.PrivateMemorySize64 / (1024 * 1024)}, {managed}, mode={(deferred ? "background" : "compacting")}, gen2Delta={GC.CollectionCount(2) - beforeGen2}, coalesced={coalesced}, elapsedMs={stopwatch.ElapsedMilliseconds}.");
        }
        catch (Exception error)
        {
            AppLog.Error($"Memory trim failed ({reason})", error);
        }
    }

    private static void RequestStoppedRecordingTrim()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(StopSettleDelay).ConfigureAwait(false);

            // Recording came back before the delay elapsed - leave the heap
            // alone rather than stopping the capture threads we just restarted.
            if (Recording) return;
            RequestTrim("recording stopped");
        });
    }
}
