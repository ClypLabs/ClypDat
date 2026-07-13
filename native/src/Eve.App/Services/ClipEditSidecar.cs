using System.Text.Json;
using Eve.Core.Settings;

namespace Eve.App.Services;

// Per-clip trim/volume edit state used to live in the global settings.json under
// %LocalAppData%\EVE, keyed by clip path. That meant it didn't travel with the
// clip if the user moved, backed up, or copied their library to another machine.
// Storing it as a sidecar file next to the video itself keeps it with the clip.
public static class ClipEditSidecar
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

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
            return JsonSerializer.Deserialize<ClipEditSettings>(File.ReadAllText(path));
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
}
