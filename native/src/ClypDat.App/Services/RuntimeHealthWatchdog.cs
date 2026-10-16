using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace ClypDat.App.Services;

// "It locks up if it's been running a while and then you try to do something"
// is the one class of bug the logs could never explain: whatever froze the UI
// also stopped anything on the UI thread from writing a line about it, so the
// log just ends mid-session and the user force-restarts.
//
// This runs off the UI thread entirely. It pings the dispatcher once a second
// and reports how long a ping went unanswered, so a freeze leaves a record of
// exactly when it started and how long it lasted. Alongside that it samples
// the process resources that separate the two possible causes: a deadlock
// (counts flat, UI stalled) from exhaustion (handles/threads/memory climbing
// steadily for hours before the stall).
public static class RuntimeHealthWatchdog
{
    // A UI thread that hasn't serviced a Background-priority post in this long
    // is not merely busy - a full layout pass on the heaviest library view is
    // orders of magnitude under this.
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResourceSampleInterval = TimeSpan.FromMinutes(5);
    // A stall this long is a wedge, not slow work, and the log alone cannot say
    // where (a freeze just after a hover preview left nothing but "stalled"
    // before the user killed the app). The dump holds every thread's stack,
    // managed and native. It carries the private heap - managed stacks cannot
    // be walked without it - so it runs to the process's private size; capped
    // per session and on disk so a bad day cannot fill the drive.
    private static readonly TimeSpan HangDumpThreshold = TimeSpan.FromSeconds(15);
    private const int MaxHangDumpsPerSession = 1;
    private const int HangDumpsKept = 2;
    private static int _hangDumpsWritten;

    private static int _started;
    // Written by the UI thread, read by the watchdog thread - a plain captured
    // local would be a torn read across two threads with no barrier.
    private static int _pongReceived = 1;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ClypDat.HealthWatchdog",
            Priority = ThreadPriority.BelowNormal
        };
        thread.Start();
    }

    private static void Loop()
    {
        var pingSentAt = Stopwatch.GetTimestamp();
        var stallReported = false;
        var stallDumped = false;
        var lastResourceSample = DateTime.UtcNow;

        while (true)
        {
            Thread.Sleep(1000);

            try
            {
                if (Volatile.Read(ref _pongReceived) == 1)
                {
                    Volatile.Write(ref _pongReceived, 0);
                    stallReported = false;
                    stallDumped = false;
                    pingSentAt = Stopwatch.GetTimestamp();
                    var sentAt = pingSentAt;
                    // Background priority on purpose: it queues behind the
                    // real work the UI is doing, so this measures "the UI
                    // thread is wedged", not "the UI thread is busy for a
                    // frame".
                    Dispatcher.UIThread.Post(() =>
                    {
                        var waited = Stopwatch.GetElapsedTime(sentAt);
                        if (waited >= StallThreshold)
                        {
                            AppLog.Info($"UI thread recovered after {waited.TotalSeconds:0.#}s stalled.");
                        }

                        Volatile.Write(ref _pongReceived, 1);
                    }, DispatcherPriority.Background);
                }
                else
                {
                    var waited = Stopwatch.GetElapsedTime(pingSentAt);
                    if (waited >= StallThreshold && !stallReported)
                    {
                        stallReported = true;
                        // Logged once per stall, not once per second: a long
                        // freeze should leave one clear marker plus the
                        // recovery line, not a wall of duplicates.
                        AppLog.Info($"UI thread stalled: no response for {waited.TotalSeconds:0.#}s. {DescribeResources()}");
                    }
                    if (waited >= HangDumpThreshold && !stallDumped)
                    {
                        stallDumped = true;
                        WriteHangDump(waited);
                    }
                }

                if (DateTime.UtcNow - lastResourceSample >= ResourceSampleInterval)
                {
                    lastResourceSample = DateTime.UtcNow;
                    AppLog.Debug($"Runtime health: {DescribeResources()}");
                }
            }
            catch
            {
                // A watchdog that can take the app down with it is worse than
                // no watchdog.
            }
        }
    }

    // Written in-process with dbghelp from this thread: the UI is already
    // frozen. createdump used to do this, but from .NET 10 it no longer takes
    // a pid ("The pid argument is no longer supported"), so every hang dump
    // failed and the one freeze it existed for left nothing behind.
    private static void WriteHangDump(TimeSpan waited)
    {
        if (!OperatingSystem.IsWindows() || Interlocked.Increment(ref _hangDumpsWritten) > MaxHangDumpsPerSession) return;
        try
        {
            var folder = Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "hang-dumps");
            Directory.CreateDirectory(folder);
            foreach (var old in new DirectoryInfo(folder).GetFiles("*.dmp").OrderByDescending(file => file.LastWriteTimeUtc).Skip(HangDumpsKept - 1))
            {
                try { old.Delete(); } catch { }
            }
            var path = Path.Combine(folder, $"ui-hang-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
            var clock = Stopwatch.StartNew();
            bool written;
            int error;
            using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var process = Process.GetCurrentProcess())
            {
                written = MiniDumpWriteDump(process.Handle, (uint)Environment.ProcessId, file.SafeFileHandle,
                    HangDumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                error = written ? 0 : Marshal.GetLastWin32Error();
            }
            if (!written)
            {
                try { File.Delete(path); } catch { }
                AppLog.Info($"UI hang dump failed: MiniDumpWriteDump error={error}.");
                return;
            }
            var size = new FileInfo(path).Length / (1024 * 1024);
            AppLog.Info($"UI hang dump written after {waited.TotalSeconds:0}s stalled: {path} ({size} MB, {clock.ElapsedMilliseconds}ms).");
        }
        catch (Exception error)
        {
            AppLog.Info($"UI hang dump failed: {error.Message}");
        }
    }

    // What createdump's default "withheap" dump holds: enough for dotnet-dump
    // or WinDbg's SOS to walk managed stacks and see which lock each waits on.
    private const uint HangDumpType =
        0x00000001 | // MiniDumpWithDataSegs
        0x00000004 | // MiniDumpWithHandleData
        0x00000020 | // MiniDumpWithUnloadedModules
        0x00000200 | // MiniDumpWithPrivateReadWriteMemory
        0x00000800 | // MiniDumpWithFullMemoryInfo
        0x00001000 | // MiniDumpWithThreadInfo
        0x00040000;  // MiniDumpWithTokenInformation

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, Microsoft.Win32.SafeHandles.SafeFileHandle file,
        uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    private static string DescribeResources()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            // Kernel handles and GDI objects are the two exhaustion ceilings a
            // long-running Windows app actually hits (GDI is capped at 10,000
            // per process by default, and blows up as silent draw failures and
            // freezes rather than an exception).
            var gdi = GetGuiResources(process.Handle, 0);
            var user = GetGuiResources(process.Handle, 1);
            return $"managedMb={GC.GetTotalMemory(false) / (1024 * 1024)}, " +
                   $"workingSetMb={process.WorkingSet64 / (1024 * 1024)}, " +
                   $"privateMb={process.PrivateMemorySize64 / (1024 * 1024)}, " +
                   $"handles={process.HandleCount}, threads={process.Threads.Count}, " +
                   $"gdi={gdi}, user={user}, " +
                   $"gen0={GC.CollectionCount(0)}, gen1={GC.CollectionCount(1)}, gen2={GC.CollectionCount(2)}, " +
                   $"uptimeMin={(DateTime.Now - process.StartTime).TotalMinutes:0}";
        }
        catch (Exception error)
        {
            return $"resource sample failed: {error.Message}";
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
