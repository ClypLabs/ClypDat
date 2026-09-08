using System.Net.Http;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Keeps a clip's cover art on disk beside the library's other sidecars.
///
/// Downloaded once, when the clip is saved, and never fetched again: a clip
/// exported months later has to draw the same card it always would have, and
/// Spotify's image URLs are neither permanent nor available offline.
/// </summary>
internal static class SpotifyCoverArtStore
{
    public static async Task<SpotifyTimeline> FetchTimelineAsync(string root, string clip, SpotifyTimeline timeline, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(16));
        using var slots = new SemaphoreSlim(4);
        var urls = timeline.Samples.Select(item => item.ArtUrl).Where(url => !string.IsNullOrWhiteSpace(url)).Distinct().ToArray();
        var folder = LibraryLayout.SidecarPath(root, clip, ".spotify-art");
        var tasks = urls.Select(async url =>
        {
            var entered = false;
            try
            {
                await slots.WaitAsync(deadline.Token).ConfigureAwait(false);
                entered = true;
                var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url!)));
                var path = Path.Combine(folder, key + ".jpg");
                if (!File.Exists(path))
                {
                    using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) return (url, path: (string?)null);
                    await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                    using var data = new MemoryStream();
                    var buffer = new byte[16384];
                    int read;
                    while ((read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                    {
                        if (data.Length + read > 8 * 1024 * 1024) return (url, path: (string?)null);
                        data.Write(buffer, 0, read);
                    }
                    Directory.CreateDirectory(folder);
                    var temporary = path + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temporary, data.ToArray(), deadline.Token).ConfigureAwait(false);
                        File.Move(temporary, path, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                return (url, path: (string?)path);
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { return (url, path: (string?)null); }
            finally { if (entered) slots.Release(); }
        });
        var paths = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToDictionary(item => item.url!, item => item.path);
        token.ThrowIfCancellationRequested();
        return timeline with { Samples = timeline.Samples.Select(item => item with {
            ArtPath = item.ArtUrl is { } url && paths.TryGetValue(url, out var path) ? path : item.ArtPath, ArtUrl = null }).ToArray() };
    }
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Where a clip's art lives, whether or not it exists yet.</summary>
    public static string PathFor(string libraryRoot, string clipPath) =>
        LibraryLayout.SidecarPath(libraryRoot, clipPath, ".cover.jpg");

    /// <summary>The art for a clip, or null when it has none on disk.</summary>
    public static string? Existing(string libraryRoot, string clipPath)
    {
        try
        {
            var path = PathFor(libraryRoot, clipPath);
            return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Downloads the art and records it on the clip's sidecar. Best effort
    /// throughout - a missing cover costs the card its picture and nothing else.
    /// </summary>
    public static async Task FetchAsync(string libraryRoot, string clipPath, string? artUrl)
    {
        if (string.IsNullOrWhiteSpace(artUrl) || string.IsNullOrWhiteSpace(clipPath)) return;
        if (Existing(libraryRoot, clipPath) is not null) return;

        try
        {
            var path = PathFor(libraryRoot, clipPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var bytes = await Http.GetByteArrayAsync(artUrl).ConfigureAwait(false);
            if (bytes.Length == 0) return;
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);

            // Re-read rather than pass the record in: the sidecar may have been
            // rewritten while the download was in flight, and the art is the
            // only field this owns.
            var info = ClipInfoSidecar.Load(libraryRoot, clipPath);
            if (info is null) return;
            ClipInfoSidecar.Save(libraryRoot, clipPath, info with { SpotifyArtPath = path });
        }
        catch (Exception error)
        {
            AppLog.Error($"Spotify: could not store the cover art for '{clipPath}'.", error);
        }
    }
}
