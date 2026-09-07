using System.Text.Json;

namespace ClypDat.App.Services;

/// <summary>Versioned, clip-relative Spotify replay history.</summary>
internal sealed record SpotifyTimeline(int Version, IReadOnlyList<SpotifyTimelineSample> Samples)
{
    public const int CurrentVersion = 1;
}

internal sealed record SpotifyTimelineSample(
    double OffsetSeconds, string? TrackId, string? Track, string? Artist, string? Album,
    int? DurationMs, int? ProgressMs, bool IsPlaying, string? ArtPath, bool Available);

internal static class SpotifyTimelineSidecar
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public static string PathFor(string libraryRoot, string clipPath) => LibraryLayout.SidecarPath(libraryRoot, clipPath, ".spotify.json");

    public static SpotifyTimeline? Load(string libraryRoot, string clipPath)
    {
        try
        {
            var path = PathFor(libraryRoot, clipPath);
            if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024) return null;
            var value = JsonSerializer.Deserialize<SpotifyTimeline>(File.ReadAllText(path));
            return value is { Version: SpotifyTimeline.CurrentVersion } ? value : null;
        }
        catch (Exception error) { AppLog.Error("Spotify timeline read failed.", error); return null; }
    }

    public static void Save(string libraryRoot, string clipPath, IEnumerable<SpotifyTimelineSample> samples)
    {
        try
        {
            var path = PathFor(libraryRoot, clipPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var ordered = samples.Where(item => double.IsFinite(item.OffsetSeconds) && item.OffsetSeconds >= 0)
                .OrderBy(item => item.OffsetSeconds).TakeLast(4096).ToArray();
            File.WriteAllText(path, JsonSerializer.Serialize(new SpotifyTimeline(SpotifyTimeline.CurrentVersion, ordered), Json));
        }
        catch (Exception error) { AppLog.Error("Spotify timeline save failed.", error); }
    }

    /// <summary>State at source time. Only playing samples advance progress.</summary>
    public static SpotifyTimelineSample? At(SpotifyTimeline? timeline, double offsetSeconds)
    {
        var sample = timeline?.Samples.LastOrDefault(item => item.OffsetSeconds <= offsetSeconds);
        if (sample is null || !sample.Available) return null;
        if (!sample.IsPlaying || sample.ProgressMs is null) return sample;
        var next = timeline!.Samples.FirstOrDefault(item => item.OffsetSeconds > sample.OffsetSeconds);
        var seconds = Math.Max(0, offsetSeconds - sample.OffsetSeconds);
        if (next is not null) seconds = Math.Min(seconds, Math.Max(0, next.OffsetSeconds - sample.OffsetSeconds));
        var progress = sample.ProgressMs.Value + (int)Math.Round(seconds * 1000);
        if (sample.DurationMs is { } length) progress = Math.Min(progress, length);
        return sample with { ProgressMs = progress };
    }

    public static void Delete(string libraryRoot, string clipPath)
    {
        try { var path = PathFor(libraryRoot, clipPath); if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
