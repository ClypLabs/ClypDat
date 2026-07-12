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

    // Lives in a "Clip Info" subfolder next to the clip instead of directly beside
    // it - same reasoning as ClipInfoSidecar, keeps the library folder from filling
    // up with clip.mp4 + clip.mp4.eve.json pairs.
    public static string SidecarPath(string clipPath)
    {
        var directory = Path.GetDirectoryName(clipPath) ?? string.Empty;
        return Path.Combine(directory, "Clip Info", Path.GetFileName(clipPath) + ".eve.json");
    }

    public static void Save(string clipPath, ClipEditSettings edit)
    {
        try
        {
            var sidecarPath = SidecarPath(clipPath);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(edit, SerializerOptions));
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip edit sidecar save failed: {clipPath}", error);
        }
    }

    public static ClipEditSettings? Load(string clipPath)
    {
        var path = SidecarPath(clipPath);
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

    public static void Delete(string clipPath)
    {
        try
        {
            var path = SidecarPath(clipPath);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip edit sidecar delete failed: {clipPath}", error);
        }
    }
}
