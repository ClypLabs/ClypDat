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

    // ".eve.json" kept as-is post-rebrand (not ".clypdat.json") - every existing
    // clip already has one of these on disk, and this suffix is purely an
    // internal, hidden (.clipinfo folder) implementation detail never shown to
    // a user. Renaming it would silently orphan every existing clip's saved
    // trim/edit state for zero visible benefit.
    public static string SidecarPath(string libraryRoot, string clipPath)
    {
        return LibraryLayout.SidecarPath(libraryRoot, clipPath, ".eve.json");
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
        if (!File.Exists(path)) path = LibraryLayout.LegacySidecarPath(clipPath, ".eve.json");
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
            var paths = new[] { SidecarPath(libraryRoot, clipPath), LibraryLayout.LegacySidecarPath(clipPath, ".eve.json") };
            foreach (var path in paths.Where(File.Exists)) File.Delete(path);
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip edit sidecar delete failed: {clipPath}", error);
        }
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
