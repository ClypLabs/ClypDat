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

    private static int _started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
    }

    // Also called directly at the points where a large, known-finished
    // allocation burst just ended (closing the editor, finishing a clip save),
    // rather than waiting out the idle timer for memory we already know is
    // dead.
    public static void Trim(string reason)
    {
        try
        {
            long beforePrivate;
            using (var before = Process.GetCurrentProcess())
            {
                beforePrivate = before.PrivateMemorySize64;
            }

            var beforeManaged = GC.GetTotalMemory(false);

            AudioChunkCache.Clear();
            BitmapCache.Clear();

            if (Recording) return;

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var settled = Process.GetCurrentProcess();
            AppLog.Info(
                $"Memory cleanup ({reason}): privateMb {beforePrivate / (1024 * 1024)} -> {settled.PrivateMemorySize64 / (1024 * 1024)}, managedMb {beforeManaged / (1024 * 1024)} -> {GC.GetTotalMemory(false) / (1024 * 1024)}.");
        }
        catch (Exception error)
        {
            AppLog.Error($"Memory trim failed ({reason})", error);
        }
    }

    private static void RequestTrim(string reason)
    {
        _ = Task.Run(() => Trim(reason));
    }
}
