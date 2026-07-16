using System.Text.Json;

namespace Eve.App.Services;

// The game a clip was captured from and, for a CS2 auto-clip, which event
// triggered it (e.g. "3K", "Ace", "Headshot", "Death") - written once at save
// time so the library can show the actual game name and a per-event icon on
// the tile instead of parsing it back out of the clip's filename, which for a
// manual clip is just the game name and for an auto-clip is "<event> - <map>".
// CustomTitle is a separate, user-set display label shown in place of "Clip
// from {date}" for non-auto-clip cards (manual clips, VODs, Medal imports) -
// deliberately independent of FileTitle/GameDisplayName so renaming a clip
// never touches the game association or, for a Medal import, its original
// event title (e.g. "4K - Inferno").
public sealed record ClipInfo(
    string? GameDisplayName,
    string? AutoClipEventType,
    string? FileTitle = null,
    DateTimeOffset? CapturedAt = null,
    string? MedalImportKey = null,
    string? CustomTitle = null);

public static class ClipInfoSidecar
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static string SidecarPath(string libraryRoot, string clipPath)
    {
        return LibraryLayout.SidecarPath(libraryRoot, clipPath, ".info.json");
    }

    public static void Save(string libraryRoot, string clipPath, ClipInfo info)
    {
        try
        {
            var sidecarPath = SidecarPath(libraryRoot, clipPath);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(info, SerializerOptions));
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip info sidecar save failed: {clipPath}", error);
        }
    }

    public static ClipInfo? Load(string libraryRoot, string clipPath)
    {
        var path = SidecarPath(libraryRoot, clipPath);
        if (!File.Exists(path)) path = LibraryLayout.LegacySidecarPath(clipPath, ".info.json");
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

    public static void Delete(string libraryRoot, string clipPath)
    {
        try
        {
            var paths = new[] { SidecarPath(libraryRoot, clipPath), LibraryLayout.LegacySidecarPath(clipPath, ".info.json") };
            foreach (var path in paths.Where(File.Exists)) File.Delete(path);
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip info sidecar delete failed: {clipPath}", error);
        }
    }
}
