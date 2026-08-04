using System.Collections.Concurrent;
using System.Text;
using System.Reflection;

namespace ClypDat.App.Services;

// Every log line used to be a synchronous File.AppendAllText - open the file,
// write, close - under one process-wide lock, on whatever thread called it.
// That put the UI thread (editor seek/pause/hover all log) in a queue behind
// the capture threads' 2s diagnostics and the clip-save pipeline's ffmpeg
// output, all writing to the same disk that is simultaneously taking a
// multi-hundred-megabyte clip flush. A slow append there froze every other
// logger with it, UI included, for as long as the disk took.
//
// Now callers only enqueue. One background thread owns the files, keeps their
// handles open, and batches whatever has piled up. Nothing on a hot path ever
// touches the disk or waits on another thread's write.
public static class AppLog
{
    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClypDat",
        "logs");

    private sealed record PendingLine(string Path, string Line, bool Droppable);

    private static readonly ConcurrentQueue<PendingLine> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    // Set by the writer at the end of every pass, once whatever it dequeued is
    // actually on disk - Flush() waits on this rather than just on the queue
    // emptying, which happens before the StreamWriter buffer is pushed out.
    private static readonly ManualResetEventSlim Flushed = new(true);
    private static int _pendingCount;
    private static int _droppedCount;

    // A backlog this deep means the disk is not keeping up at all, and the
    // only thing holding a deeper queue would achieve is turning a logging
    // problem into a memory problem. Debug telemetry is dropped first; INFO
    // and ERROR - the lines that explain what actually happened - are kept
    // until the queue is an order of magnitude past this.
    private const int DroppableBacklog = 20_000;
    private const int HardBacklog = 200_000;

    static AppLog()
    {
        var writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "ClypDat.AppLog",
            // Below the UI and capture threads on purpose: logging is never
            // the reason anything else should wait for a core.
            Priority = ThreadPriority.BelowNormal
        };
        writer.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    // High-frequency diagnostics (capture stats, per-seek traces, per-chunk
    // timings) - written to a separate clypdat-debug-{date}.log so the main
    // clypdat-{date}.log stays a readable story of what the app actually did
    // (startup, buffer on/off, clips saved, errors) instead of a wall of
    // engine telemetry. Same folder, same 7-day prune.
    public static void Debug(string message)
    {
        Write("DEBUG", message, debugFile: true, droppable: true);
    }

    public static void Error(string message, Exception? error = null)
    {
        Write("ERROR", error is null ? message : $"{message}: {error}");
    }

    public static void OpenFolder()
    {
        Directory.CreateDirectory(LogFolder);
        ExplorerService.Open(LogFolder, selectFile: false);
    }

    public static void Startup()
    {
        var path = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? "unknown";
        var timestamp = File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss") : "missing";
        Info($"App startup: path={path}, timestamp={timestamp}, version={Assembly.GetEntryAssembly()?.GetName().Version}.");
        PruneOldLogs();
    }

    // Drains whatever is queued and pushes it to disk before returning. Only
    // for the points where losing the tail actually matters (process exit, a
    // crash handler about to tear the process down) - never on a hot path,
    // since waiting for the disk is the entire thing this class now avoids.
    public static void Flush()
    {
        Flushed.Reset();
        Signal.Set();
        Flushed.Wait(TimeSpan.FromSeconds(2));
    }

    // One clypdat-{date}.log file is created per calendar day and nothing ever
    // deleted the old ones - left running for a couple weeks this folder just
    // grows forever (a real case measured several MB/day). Called once at
    // startup, not per-write, so this is a cheap one-time sweep, not a cost
    // paid on every log line.
    private const int LogRetentionDays = 7;

    private static void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-LogRetentionDays);
            foreach (var file in Directory.EnumerateFiles(LogFolder, "clypdat-*.log"))
            {
                if (File.GetLastWriteTime(file).Date < cutoff)
                {
                    try { File.Delete(file); } catch { /* best effort */ }
                }
            }
        }
        catch
        {
            // Logging must never break capture/editor flow.
        }
    }

    private static void Write(string level, string message, bool debugFile = false, bool droppable = false)
    {
        try
        {
            var backlog = Volatile.Read(ref _pendingCount);
            if (backlog >= (droppable ? DroppableBacklog : HardBacklog))
            {
                Interlocked.Increment(ref _droppedCount);
                return;
            }

            var path = Path.Combine(LogFolder, $"clypdat-{(debugFile ? "debug-" : string.Empty)}{DateTime.Now:yyyy-MM-dd}.log");
            // One entry = one line, always. Embedded multi-line payloads
            // (ffmpeg stderr, exception stack traces) otherwise splatter
            // timestamp-less fragments through the file and make it unreadable.
            message = message.ReplaceLineEndings(" ¦ ");
            // Keep head AND tail, not just head - ffmpeg errors put version/config
            // banner (often 1000+ chars alone) up front and the actual failure
            // reason (Conversion failed!, Error while filtering, etc.) at the very
            // end, so a head-only truncation was silently hiding the one part that
            // explains WHY every ffmpeg failure happened.
            if (message.Length > 2000)
            {
                message = message[..1200] + " …[truncated]… " + message[^700..];
            }

            // Timestamp is taken here, not on the writer thread, so a batched
            // line still reports when it happened rather than when it landed.
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            Pending.Enqueue(new PendingLine(path, line, droppable));
            Interlocked.Increment(ref _pendingCount);
            Signal.Set();
        }
        catch
        {
            // Logging must never break capture/editor flow.
        }
    }

    private static void WriterLoop()
    {
        var writers = new Dictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);
        var lastUsed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(LogFolder);
        }
        catch
        {
            // Retried per-write below.
        }

        while (true)
        {
            Signal.WaitOne(TimeSpan.FromMilliseconds(500));

            var wrote = false;
            while (Pending.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _pendingCount);
                try
                {
                    if (!writers.TryGetValue(pending.Path, out var writer))
                    {
                        Directory.CreateDirectory(LogFolder);
                        // FileShare.ReadWrite so tailing the log in another
                        // program (or the app's own log-dump export) never
                        // makes a write fail.
                        var stream = new FileStream(
                            pending.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 64 * 1024);
                        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
                        writers[pending.Path] = writer;
                    }

                    writer.Write(pending.Line);
                    lastUsed[pending.Path] = DateTime.UtcNow;
                    wrote = true;
                }
                catch
                {
                    // A failed write must not kill the writer thread - drop the
                    // line and reopen the file on the next one.
                    if (writers.Remove(pending.Path, out var broken))
                    {
                        try { broken.Dispose(); } catch { /* best effort */ }
                    }
                }
            }

            var dropped = Interlocked.Exchange(ref _droppedCount, 0);
            if (dropped > 0)
            {
                // Reported rather than silently swallowed: a nonzero count here
                // is itself the diagnosis (the disk could not keep up), and it
                // explains any gap in the surrounding telemetry.
                Write("INFO", $"Log backlog: dropped {dropped} lines because the log queue was full.");
            }

            if (wrote)
            {
                foreach (var writer in writers.Values)
                {
                    try { writer.Flush(); } catch { /* best effort */ }
                }
            }

            // Signalled even on an empty pass, so a Flush() with nothing queued
            // returns immediately instead of sitting out its whole timeout.
            Flushed.Set();
            if (!wrote) continue;

            // Close a day's file once it stops being written to (date rollover,
            // or debug logging going quiet) instead of holding every handle for
            // the life of the process.
            foreach (var path in lastUsed.Where(pair => DateTime.UtcNow - pair.Value > TimeSpan.FromMinutes(5))
                         .Select(pair => pair.Key).ToArray())
            {
                if (writers.Remove(path, out var stale))
                {
                    try { stale.Dispose(); } catch { /* best effort */ }
                }

                lastUsed.Remove(path);
            }
        }
    }
}
