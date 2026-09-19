using System.Security.Cryptography;
using System.Text;

namespace ClypDat.App.Services;

public static class LibraryLayout
{
    public const double ClipMaximumDurationSeconds = 300;
    // Version 2 folds the former "Saved Clips" editor-export folder into the
    // normal per-game Clips layout.
    public const int CurrentVersion = 2;

    public static string ClipsRoot(string libraryRoot) => Path.Combine(libraryRoot, "Clips");
    public static string VodsRoot(string libraryRoot) => Path.Combine(libraryRoot, "VODs");
    public static string ClipInfoRoot(string libraryRoot) => Path.Combine(libraryRoot, ".clipinfo");

    public static string VideoDirectory(string libraryRoot, TimeSpan duration, string gameDisplayName)
    {
        var category = duration.TotalSeconds > ClipMaximumDurationSeconds ? VodsRoot(libraryRoot) : ClipsRoot(libraryRoot);
        return Path.Combine(category, ClipFileNaming.BuildBaseName(string.IsNullOrWhiteSpace(gameDisplayName) ? "Unknown Game" : gameDisplayName));
    }

    public static string VodDirectory(string libraryRoot, string gameDisplayName) =>
        Path.Combine(VodsRoot(libraryRoot), ClipFileNaming.BuildBaseName(string.IsNullOrWhiteSpace(gameDisplayName) ? "Unknown Game" : gameDisplayName));

    public static string SidecarPath(string libraryRoot, string videoPath, string suffix)
    {
        // Path.GetRelativePath returns the SECOND path unchanged when the two have
        // different roots - another drive, or a UNC share. That result does not start
        // with "..", so the old containment test passed it straight through, and
        // Path.Combine(root, "D:\x\y.mp4.info.json") discards its first argument
        // because the second is rooted. The sidecar was then created next to the clip,
        // outside the library, and later deleted from there.
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot)) + Path.DirectorySeparatorChar;
        var fullVideo = Path.GetFullPath(videoPath);

        string relative;
        if (fullVideo.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            relative = fullVideo[fullRoot.Length..];
        }
        else
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullVideo)))[..24].ToLowerInvariant();
            relative = Path.Combine("external", hash + Path.GetExtension(videoPath));
        }

        var path = Path.Combine(ClipInfoRoot(libraryRoot), relative + suffix);

        // Belt and braces: whatever the composition produced must still be inside
        // .clipinfo, or the sidecar does not get written there at all.
        var infoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ClipInfoRoot(libraryRoot))) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(infoRoot, StringComparison.OrdinalIgnoreCase))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullVideo)))[..24].ToLowerInvariant();
            path = Path.Combine(ClipInfoRoot(libraryRoot), "external", hash + Path.GetExtension(videoPath) + suffix);
        }
        EnsureClipInfoRoot(libraryRoot);
        return path;
    }

    public static void EnsureRoots(string libraryRoot)
    {
        Directory.CreateDirectory(ClipsRoot(libraryRoot));
        Directory.CreateDirectory(VodsRoot(libraryRoot));
        EnsureClipInfoRoot(libraryRoot);
        ClipEditSidecar.MigrateLegacySidecars(libraryRoot);
        SpotifyCoverArtStore.MigrateLibrary(libraryRoot);
    }

    public static void EnsureClipInfoRoot(string libraryRoot)
    {
        var directory = ClipInfoRoot(libraryRoot);
        if (!LibraryPathGuard.IsWithin(libraryRoot, directory))
            throw new InvalidDataException("The clip metadata directory crosses a filesystem link.");
        Directory.CreateDirectory(directory);
        try { new DirectoryInfo(directory).Attributes |= FileAttributes.Hidden; }
        catch { /* Hidden is cosmetic; metadata storage still works without it. */ }
    }

    public static string LegacySidecarPath(string videoPath, string suffix)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;
        return Path.Combine(directory, "Clip Info", Path.GetFileName(videoPath) + suffix);
    }

    public static string LegacyAdjacentPausedPath(string videoPath) =>
        Path.Combine(Path.GetDirectoryName(videoPath) ?? string.Empty, Path.GetFileName(videoPath) + ".paused.json");

    public static void MoveSidecars(string libraryRoot, string oldVideoPath, string newVideoPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(oldVideoPath), Path.GetFullPath(newVideoPath), comparison)) return;
        // Copy writes a new timeline only when the source has one. Remove the
        // destination's orphan first so a clip with no music cannot adopt it.
        var destinationTimeline = SpotifyTimelineSidecar.PathFor(libraryRoot, newVideoPath);
        if (!LibraryPathGuard.IsWithin(libraryRoot, destinationTimeline))
            throw new InvalidDataException("Destination metadata crosses a filesystem link.");
        if (File.Exists(destinationTimeline)) File.Delete(destinationTimeline);
        var moves = new List<(string Source, string Destination)>();
        foreach (var suffix in new[] { ".info.json", ".json", ".paused.json", ".cover.jpg", ".source.json" })
        {
            var newPath = SidecarPath(libraryRoot, newVideoPath, suffix);
            if (!LibraryPathGuard.IsWithin(libraryRoot, newPath))
                throw new InvalidDataException("Destination metadata crosses a filesystem link.");
            var candidates = new[]
            {
                SidecarPath(libraryRoot, oldVideoPath, suffix),
                LegacySidecarPath(oldVideoPath, suffix),
                suffix == ".paused.json" ? LegacyAdjacentPausedPath(oldVideoPath) : string.Empty
            };

            var sourcePaths = candidates.Where(path => LibraryPathGuard.IsWithin(libraryRoot, path) && File.Exists(path)).ToArray();
            if (sourcePaths.Length == 0)
            {
                foreach (var stale in new[] { newPath, LegacySidecarPath(newVideoPath, suffix),
                    suffix == ".paused.json" ? LegacyAdjacentPausedPath(newVideoPath) : string.Empty })
                    if (LibraryPathGuard.IsWithin(libraryRoot, stale) && File.Exists(stale)) File.Delete(stale);
            }
            if (sourcePaths.Length > 0) moves.Add((sourcePaths[0], newPath));
        }
        // Copy needs the source info still present. Clear stale metadata first,
        // then let it create the destination's own timeline and cover.
        SpotifyTimelineSidecar.Copy(libraryRoot, oldVideoPath, newVideoPath);
        foreach (var (source, destination) in moves)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Live metadata wins; a failed move preserves its source.
            File.Move(source, destination, overwrite: true);
        }
        var info = ClipInfoSidecar.Load(libraryRoot, newVideoPath);
        if (info is not null)
        {
            var art = SpotifyTimelineSidecar.Load(libraryRoot, newVideoPath)?.Samples.FirstOrDefault(item => item.ArtPath is not null)?.ArtPath
                ?? SpotifyCoverArtStore.Existing(libraryRoot, newVideoPath);
            ClipInfoSidecar.Save(libraryRoot, newVideoPath, info with { SpotifyArtPath = art });
        }
        if (!string.Equals(oldVideoPath, newVideoPath, StringComparison.OrdinalIgnoreCase)) SpotifyTimelineSidecar.Delete(libraryRoot, oldVideoPath);
    }
}
