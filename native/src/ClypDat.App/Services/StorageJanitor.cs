namespace ClypDat.App.Services;

// Sweeps stale scratch data under %LocalAppData%\ClypDat at app startup. Each
// replay backend cleans its own scratch folder when IT starts a session - but
// switching backends (or a crash) leaves the OTHER backends' folders orphaned
// forever: a real install had 358MB in replay-buffer (the non-Windows ffmpeg
// backend, never even used on Windows) and 97MB in windows-replay-buffer left
// from before the Native backend became the default. Capture owns current-session
// files; cleanup defers while capture runs and ignores files created by this process.
public static class StorageJanitor
{
    private static readonly object Sync = new();
    private static readonly string[] ScratchFolders =
    {
        "replay-buffer",
        "windows-replay-buffer",
        "native-replay-buffer"
    };
    private static DateTime _processStartedUtc;
    private static bool _initialized;
    private static bool _cleanupRunning;
    private static bool _cleanupCompleted;
    private static bool _cleanupQueued;

    static StorageJanitor()
    {
        CaptureBackgroundWorkGate.StateChanged += OnCaptureStateChanged;
    }

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized) return;
            _processStartedUtc = DateTime.UtcNow;
            _initialized = true;
        }
    }

    public static void CleanupAtStartup()
    {
        Initialize();
        lock (Sync)
        {
            if (_cleanupCompleted || _cleanupRunning) return;
            if (CaptureBackgroundWorkGate.IsCaptureActive)
            {
                if (!_cleanupQueued)
                {
                    _cleanupQueued = true;
                    AppLog.Info("Scratch cleanup deferred until replay capture stops.");
                }

                return;
            }

            _cleanupRunning = true;
            _cleanupQueued = false;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClypDat");
        try
        {
            foreach (var folder in ScratchFolders)
            {
                var path = Path.Combine(root, folder);
                if (!Directory.Exists(path)) continue;
                var removed = 0;
                long removedBytes = 0;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.LastWriteTimeUtc >= _processStartedUtc) continue;
                            var size = info.Length;
                            File.Delete(file);
                            removed++;
                            removedBytes += size;
                        }
                        catch
                        {
                            // Best effort - a file still held open is skipped.
                        }
                    }
                }
                catch (Exception error)
                {
                    AppLog.Error($"Scratch cleanup failed for {path}", error);
                }

                if (removed > 0)
                {
                    AppLog.Info($"Scratch cleanup: removed {removed} stale file(s) ({removedBytes / (1024.0 * 1024.0):0.0}MB) from {folder}.");
                }
            }
        }
        finally
        {
            lock (Sync)
            {
                _cleanupRunning = false;
                _cleanupCompleted = true;
            }
        }
    }

    internal static int DeleteFilesOlderThan(string root, DateTime cutoffUtc)
    {
        var removed = 0;
        if (!Directory.Exists(root)) return removed;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc >= cutoffUtc) continue;
            File.Delete(file);
            removed++;
        }

        return removed;
    }

    private static void OnCaptureStateChanged(bool active)
    {
        if (active) return;
        lock (Sync)
        {
            if (!_cleanupQueued || _cleanupCompleted || _cleanupRunning) return;
        }

        AppLog.Info("Scratch cleanup resumed after replay capture stopped.");
        _ = Task.Run(CleanupAtStartup);
    }
}
