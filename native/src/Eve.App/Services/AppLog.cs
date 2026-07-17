using System.Diagnostics;
using System.Text;
using System.Reflection;

namespace Eve.App.Services;

public static class AppLog
{
    private static readonly object Lock = new();

    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EVE",
        "logs");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    // High-frequency diagnostics (capture stats, per-seek traces, per-chunk
    // timings) - written to a separate eve-debug-{date}.log so the main
    // eve-{date}.log stays a readable story of what the app actually did
    // (startup, buffer on/off, clips saved, errors) instead of a wall of
    // engine telemetry. Same folder, same 7-day prune.
    public static void Debug(string message)
    {
        Write("DEBUG", message, debugFile: true);
    }

    public static void Error(string message, Exception? error = null)
    {
        Write("ERROR", error is null ? message : $"{message}: {error}");
    }

    public static void OpenFolder()
    {
        Directory.CreateDirectory(LogFolder);
        Process.Start(new ProcessStartInfo(LogFolder) { UseShellExecute = true });
    }

    public static void Startup()
    {
        var path = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? "unknown";
        var timestamp = File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss") : "missing";
        Info($"App startup: path={path}, timestamp={timestamp}, version={Assembly.GetEntryAssembly()?.GetName().Version}.");
        PruneOldLogs();
    }

    // One eve-{date}.log file is created per calendar day and nothing ever
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
            foreach (var file in Directory.EnumerateFiles(LogFolder, "eve-*.log"))
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

    private static void Write(string level, string message, bool debugFile = false)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var path = Path.Combine(LogFolder, $"eve-{(debugFile ? "debug-" : string.Empty)}{DateTime.Now:yyyy-MM-dd}.log");
            // One entry = one line, always. Embedded multi-line payloads
            // (ffmpeg stderr, exception stack traces) otherwise splatter
            // timestamp-less fragments through the file and make it unreadable.
            message = message.ReplaceLineEndings(" ¦ ");
            if (message.Length > 2000) message = message[..2000] + "…";
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Lock)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break capture/editor flow.
        }
    }
}
