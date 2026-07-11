using System.Text.Json;

namespace Eve.App.Services;

// The game a clip was captured from and, for a CS2 auto-clip, which event
// triggered it (e.g. "3K", "Ace", "Headshot", "Death") - written once at save
// time so the library can show the actual game name and a per-event icon on
// the tile instead of parsing it back out of the clip's filename, which for a
// manual clip is just the game name and for an auto-clip is "<event> - <map>".
public sealed record ClipInfo(string? GameDisplayName, string? AutoClipEventType);

public static class ClipInfoSidecar
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static string SidecarPath(string clipPath) => clipPath + ".info.json";

    public static void Save(string clipPath, ClipInfo info)
    {
        try
        {
            File.WriteAllText(SidecarPath(clipPath), JsonSerializer.Serialize(info, SerializerOptions));
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip info sidecar save failed: {clipPath}", error);
        }
    }

    public static ClipInfo? Load(string clipPath)
    {
        var path = SidecarPath(clipPath);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ClipInfo>(File.ReadAllText(path));
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip info sidecar read failed: {path}", error);
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
            AppLog.Error($"Clip info sidecar delete failed: {clipPath}", error);
        }
    }
}
