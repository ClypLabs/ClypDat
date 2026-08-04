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
