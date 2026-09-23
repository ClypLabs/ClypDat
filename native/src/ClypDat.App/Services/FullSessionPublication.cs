namespace ClypDat.App.Services;

internal static class FullSessionPublication
{
    internal static void Publish(ReplayBufferConfig config, string path)
    {
        if (!File.Exists(path)) return;
        ClipInfoSidecar.Save(config.LibraryFolder, path, new ClipInfo(config.GameDisplayName, null,
            $"Session - {config.GameDisplayName}", File.GetCreationTimeUtc(path), CaptureSource: config.CaptureSource));
    }

    internal static void Complete(ReplayBufferConfig config, string path, long durationUs)
    {
        Publish(config, path);
        if (durationUs > 0) ClipStatsReporter.Record(ClipStatKind.FullSession, durationUs / 1_000_000d);
        EnforceQuota(config);
    }

    internal static void EnforceQuota(ReplayBufferConfig config)
    {
        if (config.FullSessionQuotaGb <= 0 || string.IsNullOrWhiteSpace(config.LibraryFolder)) return;
        try
        {
            var vodsRoot = LibraryLayout.VodsRoot(config.LibraryFolder);
            if (!Directory.Exists(vodsRoot)) return;

            // Junctions are not followed: the default EnumerationOptions skips only
            // Hidden|System, so a `mklink /J` reparse point planted anywhere under the
            // user-configurable library folder would otherwise let this quota sweep
            // delete files outside the library entirely.
            var vodsEnumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                IgnoreInaccessible = true,
            };
            var vodsFullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vodsRoot)) + Path.DirectorySeparatorChar;

            var sessions = Directory.EnumerateFiles(vodsRoot, "*.*", vodsEnumeration)
                .Where(path => Path.GetFullPath(path).StartsWith(vodsFullRoot, StringComparison.OrdinalIgnoreCase))
                .Where(MediaProbeService.IsVideoFile)
                .Where(path => !File.Exists(FullSessionRecovery.Marker(path)))
                .Where(path =>
                {
                    // New sessions title as "Session - {game}"; pre-existing
                    // ones as "{game} Full Session" - quota must keep seeing both.
                    var title = ClipInfoSidecar.Load(config.LibraryFolder, path)?.FileTitle;
                    return title is not null &&
                           (title.StartsWith("Session - ", StringComparison.OrdinalIgnoreCase) ||
                            title.EndsWith("Full Session", StringComparison.OrdinalIgnoreCase));
                })
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.CreationTimeUtc)
                .ToList();

            var quotaBytes = (long)config.FullSessionQuotaGb * 1024 * 1024 * 1024;
            var totalBytes = sessions.Sum(info => info.Length);
            // Index-bounded to Count-1 so the newest session always survives.
            for (var i = 0; totalBytes > quotaBytes && i < sessions.Count - 1; i++)
            {
                var victim = sessions[i];
                try
                {
                    RecordingFileOwnership.ThrowIfActive(victim.FullName);
                    File.Delete(victim.FullName);
                    ClipInfoSidecar.Delete(config.LibraryFolder, victim.FullName);
                    ClipEditSidecar.Delete(config.LibraryFolder, victim.FullName);
                    FileCleanup.TryDelete(LibraryLayout.SidecarPath(config.LibraryFolder, victim.FullName, ".paused.json"));
                    totalBytes -= victim.Length;
                    AppLog.Info($"Full session quota: deleted oldest session {victim.Name} ({victim.Length / (1024.0 * 1024 * 1024):0.0}GB) to fit {config.FullSessionQuotaGb}GB.");
                }
                catch (Exception error)
                {
                    AppLog.Error($"Full session quota: failed deleting {victim.FullName}", error);
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Full session quota enforcement failed", error);
        }
    }
}
