using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

// ClypDat spends most of its life as a background recorder with no window in
// front of the user, and it was sitting on ~800MB of working set while doing
// it. Most of that is not leaked - it is the editor's caches (extracted audio
// chunks at ~5.76MB each, decoded thumbnails) plus a managed heap the GC has
// no reason to shrink while nothing is pressuring it. Correct, and still the
// wrong thing to hold on a machine that is also running a game.
//
// So: once the editor is closed, release what is only useful with the editor
// open, compact what is left, and hand the pages back.
//
// On the last step specifically - EmptyWorkingSet is what third-party
// "memory cleaner" tools do, and on its own it is cosmetic: it evicts pages to
// the pagefile so Task Manager reports a small number, and they fault straight
// back in the moment the app touches them, slower than before. It is only
// honest here because it runs AFTER the caches are actually dropped and the
// heap actually compacted, so the pages being released are ones that genuinely
// have nothing in them any more.
public static class MemoryTrimmer
{
    // Set by MainWindow. True when the editor is closed - i.e. when the audio
    // chunk and bitmap caches are holding data for a clip nobody is looking at.
    public static Func<bool>? CanTrim { get; set; }

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    // Trimming is not free (a compacting gen2 collection walks the whole heap),
    // so it is worth doing on a long idle stretch and not worth repeating every
    // half minute after that.
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(3);
    // Below this there is nothing meaningful to reclaim and the collection
    // would cost more than it returns.
    private const long TrimThresholdBytes = 250L * 1024 * 1024;

    private static int _started;
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

    private static void Loop()
    {
        while (true)
        {
            Thread.Sleep(CheckInterval);
            try
            {
                if (DateTime.UtcNow - _lastTrimUtc < MinimumInterval) continue;
                if (CanTrim?.Invoke() != true) continue;

                using var process = Process.GetCurrentProcess();
                if (process.WorkingSet64 < TrimThresholdBytes) continue;

                Trim("idle");
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
            long beforeWorkingSet;
            using (var before = Process.GetCurrentProcess())
            {
                beforeWorkingSet = before.WorkingSet64;
            }

            var beforeManaged = GC.GetTotalMemory(false);

            AudioChunkCache.Clear();
            BitmapCache.Clear();

            // The audio chunks are 5.76MB byte[] each, so they live on the
            // large object heap, which a normal collection sweeps but never
            // compacts - without this the freed space stays as LOH holes the
            // process keeps reserved from the OS.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            // Second pass: the first one runs the finalizers for the bitmaps
            // dropped above, and an object only becomes collectable after its
            // finalizer has run.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var after = Process.GetCurrentProcess();
            EmptyWorkingSet(after.Handle);

            using var settled = Process.GetCurrentProcess();
            AppLog.Info(
                $"Memory trimmed ({reason}): workingSetMb {beforeWorkingSet / (1024 * 1024)} -> {settled.WorkingSet64 / (1024 * 1024)}, " +
                $"managedMb {beforeManaged / (1024 * 1024)} -> {GC.GetTotalMemory(false) / (1024 * 1024)}.");
        }
        catch (Exception error)
        {
            AppLog.Error($"Memory trim failed ({reason})", error);
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
