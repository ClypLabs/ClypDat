using System.Text.Json;

namespace Eve.App.Services;

// The game a clip was captured from and, for a CS2 auto-clip, which event
// triggered it (e.g. "3K", "Ace", "Headshot", "Death") - written once at save
// time so the library can show the actual game name and a per-event icon on
// the tile instead of parsing it back out of the clip's filename, which for a
// manual clip is just the game name and for an auto-clip is "<event> - <map>".
public sealed record ClipInfo(
    string? GameDisplayName,
    string? AutoClipEventType,
    string? FileTitle = null,
    DateTimeOffset? CapturedAt = null);

public static class ClipInfoSidecar
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // Lives in a "Clip Info" subfolder next to the clip instead of directly beside
    // it - a folder full of clip.mp4 + clip.mp4.info.json pairs looked cluttered.
    public static string SidecarPath(string clipPath)
    {
        var directory = Path.GetDirectoryName(clipPath) ?? string.Empty;
        return Path.Combine(directory, "Clip Info", Path.GetFileName(clipPath) + ".info.json");
    }

    public static void Save(string clipPath, ClipInfo info)
    {
        try
        {
            var sidecarPath = SidecarPath(clipPath);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(info, SerializerOptions));
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
