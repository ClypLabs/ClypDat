using System.Text.Json;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

// Per-clip trim/volume edit state used to live in the global settings.json under
// %LocalAppData%\ClypDat, keyed by clip path. That meant it didn't travel with the
// clip if the user moved, backed up, or copied their library to another machine.
// Storing it as a sidecar file next to the video itself keeps it with the clip.
public static class ClipEditSidecar
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static string SidecarPath(string libraryRoot, string clipPath)
    {
        return LibraryLayout.SidecarPath(libraryRoot, clipPath, ".json");
    }

    public static void Save(string libraryRoot, string clipPath, ClipEditSettings edit)
    {
        try
        {
            var sidecarPath = SidecarPath(libraryRoot, clipPath);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(edit, SerializerOptions));
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip edit sidecar save failed: {clipPath}", error);
        }
    }

    public static ClipEditSettings? Load(string libraryRoot, string clipPath)
    {
        var path = SidecarPath(libraryRoot, clipPath);
        if (!File.Exists(path)) path = MigrateLegacySidecar(libraryRoot, clipPath);
        if (!File.Exists(path)) return null;
        try
        {
            var json = ReadBounded(path);
            return json is null ? null : JsonSerializer.Deserialize<ClipEditSettings>(json);
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip edit sidecar read failed: {path}", error);
            return null;
        }
    }

    public static void Delete(string libraryRoot, string clipPath)
    {
        try
        {
            var paths = new[] { SidecarPath(libraryRoot, clipPath), LegacySidecarPath(libraryRoot, clipPath), LibraryLayout.LegacySidecarPath(clipPath, ".eve.json") };
            foreach (var path in paths.Where(File.Exists)) File.Delete(path);
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip edit sidecar delete failed: {clipPath}", error);
        }
    }

    /// <summary>Renames all current-library legacy edit sidecars to clip.mp4.json.</summary>
    public static void MigrateLegacySidecars(string libraryRoot)
    {
        var sidecarRoot = LibraryLayout.ClipInfoRoot(libraryRoot);
        if (!Directory.Exists(libraryRoot)) return;
        try
        {
            foreach (var legacyPath in Directory.EnumerateFiles(libraryRoot, "*.eve.json", SearchOption.AllDirectories))
                MigrateFile(legacyPath, MigrationTarget(libraryRoot, sidecarRoot, legacyPath));
        }
        catch (Exception error)
        {
            AppLog.Error("Legacy clip edit sidecar migration failed.", error);
        }
    }

    private static string MigrateLegacySidecar(string libraryRoot, string clipPath)
    {
        var target = SidecarPath(libraryRoot, clipPath);
        if (File.Exists(target)) return target;
        var legacy = LegacySidecarPath(libraryRoot, clipPath);
        if (!File.Exists(legacy)) legacy = LibraryLayout.LegacySidecarPath(clipPath, ".eve.json");
        if (!File.Exists(legacy)) return target;
        try
        {
            MigrateFile(legacy, target);
            return File.Exists(target) ? target : legacy;
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip edit sidecar migration failed: {clipPath}", error);
            return legacy;
        }
    }

    private static string LegacySidecarPath(string libraryRoot, string clipPath) =>
        LibraryLayout.SidecarPath(libraryRoot, clipPath, ".eve.json");

    private static string MigrationTarget(string libraryRoot, string sidecarRoot, string legacyPath)
    {
        var directory = Path.GetDirectoryName(legacyPath)!;
        if (string.Equals(directory, sidecarRoot, StringComparison.OrdinalIgnoreCase) ||
            directory.StartsWith(sidecarRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return legacyPath[..^".eve.json".Length] + ".json";

        // Early ClypDat builds put .clipinfo beside each game's clips. Move
        // those records into the current library-wide metadata tree too.
        var clipDirectory = Directory.GetParent(directory)?.FullName ?? libraryRoot;
        var clipName = Path.GetFileName(legacyPath[..^".eve.json".Length]);
        return SidecarPath(libraryRoot, Path.Combine(clipDirectory, clipName));
    }

    private static void MigrateFile(string legacyPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (File.Exists(targetPath)) File.Delete(legacyPath);
        else File.Move(legacyPath, targetPath);
    }

    public static void ResetAfterSavedTrim(string libraryRoot, string clipPath)
    {
        var previous = Load(libraryRoot, clipPath);
        if (previous is null) return;
        // Timing and crop have become part of the saved video. Audio levels,
        // the independent overlay and the description remain editor state.
        Save(libraryRoot, clipPath, new ClipEditSettings
        {
            Description = previous.Description,
            TrackVolumes = previous.TrackVolumes,
            MutedTrackIndexes = previous.MutedTrackIndexes,
            SpotifyOverlayVisible = previous.SpotifyOverlayVisible,
            SpotifyOverlayTransform = previous.SpotifyOverlayTransform,
            CameraOverlayVisible = previous.CameraOverlayVisible,
            CameraOverlayTransform = previous.CameraOverlayTransform,
            PeripheralOverlayVisible = previous.PeripheralOverlayVisible,
            PeripheralOverlayTransform = previous.PeripheralOverlayTransform
        });
    }

    // Sidecars are a few hundred bytes. A library refresh reads one per clip, so an
    // oversized file - however it got there - should be skipped rather than loaded.
    private const long MaximumSidecarBytes = 64 * 1024;

    private static string? ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumSidecarBytes) return null;
        return File.ReadAllText(path);
    }
}
