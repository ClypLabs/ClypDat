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
