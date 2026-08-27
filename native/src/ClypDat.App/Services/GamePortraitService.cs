using Avalonia.Media.Imaging;

namespace ClypDat.App.Services;

// Tall box art for the Custom Game Settings cards. Separate from
// GameIconService because it is a different asset for a different job: that
// one caches a ~32px icon to sit beside a name, this caches 600x900 cover art
// to be looked at.
//
// Steam publishes cover art at a fixed URL per appid, and a detection key of
// "steam-{appid}" already carries the appid - so for a Steam game no lookup is
// needed at all. Anything else falls back to the appid the curated icon list
// already maps display names to; a game in neither simply has no portrait, and
// the UI shows its icon instead.
public static class GamePortraitService
{
    private static readonly string CacheFolder = Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "game-portraits");

    // Same reasoning as GameIconService.NegativeCacheRetryAfter: without a
    // persisted miss, every settings page visit re-requests art for games that
    // have none.
    private static readonly TimeSpan NegativeCacheRetryAfter = TimeSpan.FromDays(7);

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-GamePortraits/1.0 (+https://github.com/ClypLabs/ClypDat)");
        return client;
    }

    private static string SafeFileName(string value) => string.Join("_", value.Split(Path.GetInvalidFileNameChars()));
    private static string CachePathFor(string displayName) => Path.Combine(CacheFolder, $"{SafeFileName(displayName)}.jpg");
    private static string NegativeMarkerPathFor(string displayName) => Path.Combine(CacheFolder, $"{SafeFileName(displayName)}.miss");

    public static Bitmap? TryLoad(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        try
        {
            var path = CachePathFor(displayName);
            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch (Exception error)
        {
            AppLog.Error($"Game portrait load failed for '{displayName}'", error);
            return null;
        }
    }

    /// <summary>
    /// Downloads the portrait if it is not cached yet. Returns true only when
    /// a new file was written, so callers can refresh exactly once instead of
    /// re-reading a bitmap they already have.
    /// </summary>
    public static async Task<bool> EnsureCachedAsync(string detectionKey, string displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        if (File.Exists(CachePathFor(displayName))) return false;
        if (IsNegativeCacheFresh(displayName)) return false;

        // One download per name per session even if several cards ask at once.
        lock (InFlight)
        {
            if (!InFlight.Add(displayName)) return false;
        }

        try
        {
            var url = await ResolvePortraitUrlAsync(detectionKey, displayName, cancellationToken).ConfigureAwait(false);
            if (url is null)
            {
                MarkMiss(displayName);
                return false;
            }

            Directory.CreateDirectory(CacheFolder);
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                MarkMiss(displayName);
                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 1024)
            {
                // Steam answers some missing art with a tiny placeholder rather
                // than a 404.
                MarkMiss(displayName);
                return false;
            }

            // Written via a temp file so a cancelled or failed download can
            // never leave a truncated JPEG that every later load then fails on.
            var target = CachePathFor(displayName);
            var temp = target + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            AppLog.Info($"Game portrait cached: '{displayName}'.");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error)
        {
            AppLog.Error($"Game portrait fetch failed for '{displayName}'", error);
            MarkMiss(displayName);
            return false;
        }
        finally
        {
            lock (InFlight) InFlight.Remove(displayName);
        }
    }

    // Order matters: the cheapest and most certain source first, the one that
    // costs a network search last.
    private static async Task<string?> ResolvePortraitUrlAsync(string detectionKey, string displayName, CancellationToken cancellationToken)
    {
        // 1. Steam, straight off the detection key - no lookup, no ambiguity.
        var appId = ResolveAppId(detectionKey, displayName);
        if (appId is not null) return SteamPortraitUrl(appId.Value);

        // 2. Curated art, which is the only source for a launcher exclusive
        //    that has no Steam page - see LoadCachedPortraits.
        if (RemoteGameIconsService.LoadCachedPortraits().TryGetValue(displayName, out var curated)
            && !string.IsNullOrWhiteSpace(curated))
        {
            return curated;
        }

        // 3. Epic's own launcher already caches portrait art URLs for
        //    everything in the user's library, so an Epic-only title
        //    (Fortnite, Genshin, Honkai - none of which are on Steam) resolves
        //    locally and offline.
        var epic = EpicPortraitUrl(displayName);
        if (epic is not null) return epic;

        // 4. Anything else - a Battle.net or Riot title that also ships on
        //    Steam (Call of Duty, Diablo IV), or a game launched from
        //    its own exe - by searching Steam for the name. Plenty of games
        //    that ship on other launchers also have a Steam page, and this
        //    reuses the icon service's own name matching rather than a second
        //    guess at it.
        var searched = await GameIconService.ResolveSteamAppIdForAsync(displayName, cancellationToken).ConfigureAwait(false);
        return searched is > 0 ? SteamPortraitUrl(searched.Value) : null;
    }

    // library_600x900 is Steam's portrait shelf art. Games predating it answer
    // 404, which the caller treats as a miss.
    private static string SteamPortraitUrl(int appId) =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg";

    // %ProgramData%\Epic\EpicGamesLauncher\Data\Catalog\catcache.bin is
    // base64-encoded JSON: every catalogue entry the launcher knows about, each
    // with a title and a keyImages list. DieselGameBoxTall is the 1200x1600
    // portrait. Matched on the exact title because a Fortnite library also
    // contains "Fortnite Crew", "2800 Fortnite Points" and a dozen other
    // entries a fuzzy match would happily return instead.
    private static string? EpicPortraitUrl(string displayName)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");
            if (!File.Exists(path)) return null;

            var json = Convert.FromBase64String(File.ReadAllText(path));
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("title", out var titleElement)) continue;
                if (!string.Equals(titleElement.GetString(), displayName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!entry.TryGetProperty("keyImages", out var images)) continue;

                foreach (var image in images.EnumerateArray())
                {
                    if (!image.TryGetProperty("type", out var typeElement)) continue;
                    if (!string.Equals(typeElement.GetString(), "DieselGameBoxTall", StringComparison.Ordinal)) continue;
                    if (!image.TryGetProperty("url", out var urlElement)) continue;
                    var url = urlElement.GetString();
                    if (!string.IsNullOrWhiteSpace(url)) return url;
                }
            }

            return null;
        }
        catch (Exception error)
        {
            AppLog.Error($"Epic portrait lookup failed for '{displayName}'", error);
            return null;
        }
    }

    private static int? ResolveAppId(string detectionKey, string displayName)
    {
        // The detection key IS the appid for a Steam game - no network lookup,
        // no name matching, no chance of resolving to the wrong title.
        if (!string.IsNullOrWhiteSpace(detectionKey)
            && detectionKey.StartsWith("steam-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(detectionKey.AsSpan("steam-".Length), out var keyAppId)
            && keyAppId > 0)
        {
            return keyAppId;
        }

        // Everything else leans on the curated display-name map the icon
        // service already maintains. An Epic or launcher-less game that also
        // exists on Steam picks its art up this way.
        return RemoteGameIconsService.LoadCachedAppIds().TryGetValue(displayName, out var appId) && appId > 0
            ? appId
            : null;
    }

    private static bool IsNegativeCacheFresh(string displayName)
    {
        try
        {
            var marker = new FileInfo(NegativeMarkerPathFor(displayName));
            return marker.Exists && DateTime.UtcNow - marker.LastWriteTimeUtc < NegativeCacheRetryAfter;
        }
        catch
        {
            return false;
        }
    }

    private static void MarkMiss(string displayName)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllBytes(NegativeMarkerPathFor(displayName), Array.Empty<byte>());
        }
        catch
        {
            // The marker is an optimisation; failing to write it only means the
            // lookup is retried sooner.
        }
    }
}
