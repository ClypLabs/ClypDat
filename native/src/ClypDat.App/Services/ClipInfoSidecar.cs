using System.Text.Json;

namespace ClypDat.App.Services;

// The game a clip was captured from and, for a CS2 auto-clip, which event
// triggered it (e.g. "3K", "Ace", "Headshot", "Death") - written once at save
// time so the library can show the actual game name and a per-event icon on
// the tile instead of parsing it back out of the clip's filename, which for a
// manual clip is just the game name and for an auto-clip is "<event> - <map>".
// CustomTitle is a separate, user-set display label shown in place of "Clip
// from {date}" for non-auto-clip cards (manual clips, VODs, external imports) -
// deliberately independent of FileTitle/GameDisplayName so renaming a clip
// never touches the game association or, for a Medal import, its original
// event title (e.g. "4K - Inferno").
public sealed record ClipInfo(
    string? GameDisplayName,
    string? AutoClipEventType,
    string? FileTitle = null,
    DateTimeOffset? CapturedAt = null,
    string? MedalImportKey = null,
    string? CustomTitle = null,
    // Distinguishes a re-encoded Export from the original recording - both are
    // ordinary "manual" cards (no AutoClipEventType/import provenance) with the
    // same CustomTitle-or-fallback tile logic, but an untitled export should
    // read "Exported clip from" (see ClipCardViewModel.ClipFromLabel), not
    // "Clip from", since it's a derived copy rather than the actual recording.
    bool IsExport = false,
    // Set once Save Trim has replaced a clip with a shorter range of itself.
    // The ".paused.json" sidecar records recording-pause ranges as offsets into
    // the ORIGINAL recording, so after a trim every stored offset refers to a
    // timeline the file no longer has - which is why a trimmed clip could show
    // "Playback Paused" over content that plainly was not paused. Deleting that
    // sidecar at trim time handles it going forward, but it is best-effort
    // across three possible locations and does nothing for clips trimmed before
    // that fix existed; this flag is the durable half, and makes the badge
    // impossible for a trimmed clip regardless of what sidecars survive.
    bool IsTrimmed = false,
    // Null is legacy game capture. New desktop clips use "Desktop" so editor
    // behavior does not depend on transient capture sidecars.
    string? CaptureSource = null,
    string? SteelSeriesImportKey = null);

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
            var json = ReadBounded(path);
            return json is null ? null : JsonSerializer.Deserialize<ClipInfo>(json);
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
