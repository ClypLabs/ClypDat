namespace ClypDat.App.Services;

// The Closing Safely guard can't run when the process is killed outright
// (Task Manager's End process, End task on a tray-hidden process, a crash). A
// marker file records that the last session never reached a clean quit, and a
// startup pass repairs the one UI-side operation that can be left half-done:
// Save Trim's swap of the original for the trimmed file.
internal static class UncleanExitRecovery
{
    private const string TrimBackupSuffix = ".clypdat-trim-backup";
    private static string MarkerPath => Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "session-running.marker");

    // Returns true when the previous session ended without MarkCleanExit.
    public static bool BeginSession()
    {
        var unclean = false;
        try
        {
            unclean = File.Exists(MarkerPath);
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception error)
        {
            AppLog.Error("Could not write session marker", error);
        }
        if (unclean) AppLog.Info("[Quit] previous session did not exit cleanly (killed or crashed).");
        return unclean;
    }

    public static void MarkCleanExit()
    {
        try { File.Delete(MarkerPath); }
        catch (Exception error) { AppLog.Error("Could not clear session marker", error); }
    }

    // A backup whose original is missing (or empty) means the process died
    // between the two moves in SaveTrimToOriginalAsync: put the original back.
    // With both present the trimmed file was installed and its sidecars may
    // already describe it, so neither file is touched; the backup is only
    // logged for manual recovery.
    public static void RecoverInterruptedTrims(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot)) return;
        IEnumerable<string> backups;
        try
        {
            backups = Directory.EnumerateFiles(libraryRoot, "*" + TrimBackupSuffix, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            }).ToArray();
        }
        catch (Exception error)
        {
            AppLog.Error("Trim recovery scan failed", error);
            return;
        }

        foreach (var backup in backups)
        {
            var original = backup[..^TrimBackupSuffix.Length];
            try
            {
                var originalInfo = new FileInfo(original);
                if (!originalInfo.Exists || originalInfo.Length == 0)
                {
                    File.Move(backup, original, overwrite: true);
                    AppLog.Info($"[Quit] restored clip from interrupted Save Trim: {original}");
                }
                else
                {
                    AppLog.Info($"[Quit] leftover Save Trim backup kept for manual recovery: {backup}");
                }
            }
            catch (Exception error)
            {
                AppLog.Error($"Could not recover interrupted Save Trim: {original}", error);
            }
        }
    }
}
