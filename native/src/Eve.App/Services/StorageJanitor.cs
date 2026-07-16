namespace Eve.App.Services;

// Sweeps stale scratch data under %LocalAppData%\EVE at app startup. Each
// replay backend cleans its own scratch folder when IT starts a session - but
// switching backends (or a crash) leaves the OTHER backends' folders orphaned
// forever: a real install had 358MB in replay-buffer (the non-Windows ffmpeg
// backend, never even used on Windows) and 97MB in windows-replay-buffer left
// from before the Native backend became the default. Runs before any capture
// session exists, so nothing here can race an active recording.
public static class StorageJanitor
{
    private static readonly string[] ScratchFolders =
    {
        "replay-buffer",
        "windows-replay-buffer",
        "native-replay-buffer"
    };

    public static void CleanupAtStartup()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE");

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
                        var size = new FileInfo(file).Length;
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
}
