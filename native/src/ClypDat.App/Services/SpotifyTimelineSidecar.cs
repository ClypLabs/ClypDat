using System.Text.Json;

namespace ClypDat.App.Services;

/// <summary>Versioned, clip-relative Spotify replay history.</summary>
internal sealed record SpotifyTimeline(int Version, IReadOnlyList<SpotifyTimelineSample> Samples)
{
    public const int CurrentVersion = 1;
}

internal sealed record SpotifyTimelineSample(
    double OffsetSeconds, string? TrackId, string? Track, string? Artist, string? Album,
    int? DurationMs, int? ProgressMs, bool IsPlaying, string? ArtPath, bool Available,
    double ProgressRate = 1, string? ArtUrl = null);

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
            return value is { Version: SpotifyTimeline.CurrentVersion, Samples: not null } &&
                value.Samples.All(item => double.IsFinite(item.OffsetSeconds) && item.OffsetSeconds >= 0 && double.IsFinite(item.ProgressRate) && item.ProgressRate >= 0)
                ? value with { Samples = value.Samples.OrderBy(item => item.OffsetSeconds).ToArray() } : null;
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
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new SpotifyTimeline(SpotifyTimeline.CurrentVersion, ordered), Json));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
        var progress = (int)Math.Clamp(sample.ProgressMs.Value + Math.Round(seconds * 1000 * sample.ProgressRate), 0, int.MaxValue);
        if (sample.DurationMs is { } length) progress = Math.Min(progress, length);
        return sample with { ProgressMs = progress };
    }

    public static SpotifyTimeline Rebase(SpotifyTimeline timeline, double start, double end, double speed)
    {
        speed = ClipRenderFilters.NormalizeSpeed(speed);
        var samples = new List<SpotifyTimelineSample>();
        var first = At(timeline, start);
        if (first is not null) samples.Add(first with { OffsetSeconds = 0, ProgressRate = first.ProgressRate * speed });
        else samples.Add(new(0, null, null, null, null, null, null, false, null, false));
        samples.AddRange(timeline.Samples.Where(item => item.OffsetSeconds > start && item.OffsetSeconds < end)
            .Select(item => item with { OffsetSeconds = (item.OffsetSeconds - start) / speed, ProgressRate = item.ProgressRate * speed }));
        return new(SpotifyTimeline.CurrentVersion, samples);
    }

    public static void Delete(string libraryRoot, string clipPath)
    {
        try { var path = PathFor(libraryRoot, clipPath); if (File.Exists(path)) File.Delete(path); } catch { }
        foreach (var suffix in new[] { ".cover.jpg", ".source.json" })
            try { File.Delete(LibraryLayout.SidecarPath(libraryRoot, clipPath, suffix)); } catch { }
        var art = LibraryLayout.SidecarPath(libraryRoot, clipPath, ".spotify-art");
        try { if (Directory.Exists(art)) Directory.Delete(art, true); } catch { }
    }

    public static void Copy(string root, string source, string destination, double? start = null, double? end = null, double speed = 1)
    {
        var timeline = Load(root, source);
        if (timeline is null)
        {
            var art = ClipInfoSidecar.Load(root, source)?.SpotifyArtPath ?? SpotifyCoverArtStore.Existing(root, source);
            var target = SpotifyCoverArtStore.PathFor(root, destination);
            if (art is not null && File.Exists(art) && !string.Equals(art, target, StringComparison.OrdinalIgnoreCase))
            { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(art, target, true); }
            return;
        }
        if (start is { } from && end is { } until) timeline = Rebase(timeline, from, until, speed);
        var folder = LibraryLayout.SidecarPath(root, destination, ".spotify-art");
        var paths = new Dictionary<string, string>();
        foreach (var sample in timeline.Samples)
        {
            if (sample.ArtPath is not { } old || !File.Exists(old) || paths.ContainsKey(old)) continue;
            if (old.StartsWith(SpotifyCoverArtStore.ArchiveRoot(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { paths[old] = old; continue; }
            var target = Path.Combine(folder, Path.GetFileName(old));
            Directory.CreateDirectory(folder);
            if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) File.Copy(old, target, true);
            paths[old] = target;
        }
        Save(root, destination, timeline.Samples.Select(item => item with {
            ArtPath = item.ArtPath is { } old && paths.TryGetValue(old, out var target) ? target : null }));
    }
}
