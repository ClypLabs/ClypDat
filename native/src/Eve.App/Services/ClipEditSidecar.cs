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

    public static string SidecarPath(string clipPath) => clipPath + ".eve.json";

    public static void Save(string clipPath, ClipEditSettings edit)
    {
        try
        {
            File.WriteAllText(SidecarPath(clipPath), JsonSerializer.Serialize(edit, SerializerOptions));
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
