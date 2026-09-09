using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClypDat.App.Services;

/// <summary>Permanent, library-wide Spotify artwork archive.</summary>
internal static class SpotifyCoverArtStore
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim ArchiveGate = new(1, 1);

    public static string ArchiveRoot(string root) => Path.Combine(LibraryLayout.ClipInfoRoot(root), "Song Album Covers");

    public static async Task<SpotifyTimeline> FetchTimelineAsync(string root, string clip, SpotifyTimeline timeline, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(16));
        using var slots = new SemaphoreSlim(4);
        var urls = timeline.Samples.Select(item => item.ArtUrl).Where(url => !string.IsNullOrWhiteSpace(url)).Distinct().ToArray();
        var tasks = urls.Select(async url =>
        {
            var entered = false;
            try { await slots.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true; return (url, path: await FetchToArchiveAsync(root, url!, deadline.Token).ConfigureAwait(false)); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { AppLog.Error("Spotify cover download failed.", error); return (url, path: (string?)null); }
            finally { if (entered) slots.Release(); }
        });
        var paths = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToDictionary(item => item.url!, item => item.path);
        token.ThrowIfCancellationRequested();
        // Keep URL for repair when archive file is manually removed.
        return timeline with { Samples = timeline.Samples.Select(item => item with { ArtPath = item.ArtUrl is { } url && paths.TryGetValue(url, out var path) && path is not null ? path : item.ArtPath }).ToArray() };
    }

    private static async Task<string?> FetchToArchiveAsync(string root, string url, CancellationToken token)
    {
        var index = LoadIndex(root);
        if (index.TryGetValue(url, out var file))
        {
            var known = Path.Combine(ArchiveRoot(root), file);
            if (File.Exists(known) && new FileInfo(known).Length > 0) return known;
        }
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var data = new MemoryStream(); await stream.CopyToAsync(data, token).ConfigureAwait(false);
        if (data.Length is 0 or > MaximumBytes) return null;
        var path = await ImportBytesAsync(root, data.ToArray(), token).ConfigureAwait(false);
        await ArchiveGate.WaitAsync(token).ConfigureAwait(false);
        try { index = LoadIndex(root); index[url] = Path.GetFileName(path); SaveIndex(root, index); }
        finally { ArchiveGate.Release(); }
        return path;
    }

    /// <summary>Temporary preview download; permanent clip artwork uses archive methods.</summary>
    internal static async Task<string?> DownloadAsync(string url, string path, CancellationToken token)
    {
        if (File.Exists(path)) return path;
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        if (bytes.Length is 0 or > MaximumBytes) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false); File.Move(temporary, path, true); return path; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<string> ImportFileAsync(string root, string source, CancellationToken token = default) => await ImportBytesAsync(root, await File.ReadAllBytesAsync(source, token).ConfigureAwait(false), token).ConfigureAwait(false);

    internal static async Task<string> ImportBytesAsync(string root, byte[] bytes, CancellationToken token = default)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new InvalidDataException("Spotify cover artwork is empty or too large.");
        var path = Path.Combine(ArchiveRoot(root), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".jpg");
        await ArchiveGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(ArchiveRoot(root));
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false); File.Move(temporary, path); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return path;
        }
        finally { ArchiveGate.Release(); }
    }

    /// <summary>Imports old per-clip covers, then redirects sidecars to archive copies.</summary>
    public static void MigrateLibrary(string root)
    {
        try
        {
            var infoRoot = LibraryLayout.ClipInfoRoot(root); if (!Directory.Exists(root)) return;
            var archiveRoot = ArchiveRoot(root) + Path.DirectorySeparatorChar;
            var oldFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => !Path.GetFullPath(path).StartsWith(archiveRoot, StringComparison.OrdinalIgnoreCase) && (path.EndsWith(".cover.jpg", StringComparison.OrdinalIgnoreCase) || path.Contains(".spotify-art" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var old in oldFiles) try { replacements[Path.GetFullPath(old)] = ImportFileAsync(root, old).GetAwaiter().GetResult(); } catch (Exception error) { AppLog.Error($"Spotify cover migration failed: {old}", error); }
            var sidecars = Directory.EnumerateFiles(root, "*.info.json", SearchOption.AllDirectories).Where(file => Path.GetFullPath(file).StartsWith(infoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "Clip Info", StringComparison.OrdinalIgnoreCase));
            foreach (var file in sidecars)
                try { var info = JsonSerializer.Deserialize<ClipInfo>(File.ReadAllText(file)); if (info?.SpotifyArtPath is { } old && replacements.TryGetValue(Path.GetFullPath(old), out var replacement)) WriteAtomic(file, JsonSerializer.Serialize(info with { SpotifyArtPath = replacement })); } catch (Exception error) { AppLog.Error($"Spotify info migration failed: {file}", error); }
            var timelines = Directory.EnumerateFiles(root, "*.spotify.json", SearchOption.AllDirectories).Where(file => Path.GetFullPath(file).StartsWith(infoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "Clip Info", StringComparison.OrdinalIgnoreCase));
            foreach (var file in timelines)
                try { var timeline = JsonSerializer.Deserialize<SpotifyTimeline>(File.ReadAllText(file)); if (timeline?.Samples is null) continue; var samples = timeline.Samples.Select(sample => sample.ArtPath is { } old && replacements.TryGetValue(Path.GetFullPath(old), out var replacement) ? sample with { ArtPath = replacement } : sample).ToArray(); if (!samples.SequenceEqual(timeline.Samples)) WriteAtomic(file, JsonSerializer.Serialize(timeline with { Samples = samples })); } catch (Exception error) { AppLog.Error($"Spotify timeline migration failed: {file}", error); }
            // Preserve old files: failed/restarted migrations keep recoverable data.
        }
        catch (Exception error) { AppLog.Error("Spotify cover archive migration failed.", error); }
    }

    public static string PathFor(string root, string clip) => LibraryLayout.SidecarPath(root, clip, ".cover.jpg");
    public static string? Existing(string root, string clip) { try { var path = PathFor(root, clip); return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null; } catch { return null; } }
    private static string IndexPath(string root) => Path.Combine(ArchiveRoot(root), "index.json");
    private static Dictionary<string, string> LoadIndex(string root) { try { return File.Exists(IndexPath(root)) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IndexPath(root))) ?? new() : new(); } catch { return new(); } }
    private static void SaveIndex(string root, Dictionary<string, string> index) { Directory.CreateDirectory(ArchiveRoot(root)); WriteAtomic(IndexPath(root), JsonSerializer.Serialize(index)); }
    private static void WriteAtomic(string path, string contents) { var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; try { File.WriteAllText(temporary, contents); File.Move(temporary, path, true); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }
}
