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

    // Stores repaint cover art in place: Steam keeps the same URL, so only age
    // can tell. Epic and curated art move to a new URL, which is caught at once.
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Dictionary<string, Task<bool>> InFlight = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-GamePortraits/1.0 (+https://github.com/ClypLabs/ClypDat)");
        return client;
    }

    private static string SafeFileName(string value) => string.Join("_", value.Split(Path.GetInvalidFileNameChars()));
    private static string CachePathFor(string displayName) => Path.Combine(CacheFolder, $"{SafeFileName(displayName)}.jpg");
    private static string NegativeMarkerPathFor(string displayName) => Path.Combine(CacheFolder, $"{SafeFileName(displayName)}.miss");
    // The URL a cached portrait came from, so a source that moves on is noticed.
    private static string SourcePathFor(string displayName) => Path.Combine(CacheFolder, $"{SafeFileName(displayName)}.src");

    private static string? ReadSource(string displayName)
    {
        try { var path = SourcePathFor(displayName); return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

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

    // Mods commonly ship their own title/logo art. Use it before any store
    // lookup: a name search cannot identify a renamed Psych Engine build and
    // can otherwise put unrelated game art on its settings card.
    public static Bitmap? TryLoadStandalone(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return null;
        try
        {
            var root = Path.GetDirectoryName(executablePath)!;
            var exe = Path.GetFileName(executablePath);
            if (exe.Equals("osu!.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("osu!.lazer.exe", StringComparison.OrdinalIgnoreCase))
                return GameIconService.TryLoadExecutableIcon(executablePath);

            var candidates = new[]
            {
                Path.Combine(root, "assets", "images", "titlelogo.png"),
                Path.Combine(root, "assets", "images", "logo.png"),
                Path.Combine(root, "assets", "images", "logoBumpin.png"),
                Path.Combine(root, "assets", "shared", "images", "loading_screen", "logo.png"),
                Path.Combine(root, "assets", "shared", "images", "logoBumpin.png")
            };
            var source = candidates.FirstOrDefault(File.Exists);
            return source is null ? null : new Bitmap(source);
        }
        catch (Exception error)
        {
            AppLog.Error($"Standalone game portrait load failed for '{executablePath}'", error);
            return null;
        }
    }

    // Tab badges need compact, game-specific artwork. Psych/FNF mods expose
    // their cast as health icons, unlike their generic engine executable.
    public static Bitmap? TryLoadStandaloneIcon(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return null;
        try
        {
            var root = Path.GetDirectoryName(executablePath)!;
            var icons = new[]
            {
                Path.Combine(root, "assets", "images", "icons"),
                Path.Combine(root, "assets", "shared", "images", "icons")
            };
            foreach (var folder in icons.Where(Directory.Exists))
            {
                var source = Directory.EnumerateFiles(folder, "icon-*.png", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => Path.GetFileName(path).Contains("bf", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (source is not null) return new Bitmap(source);
            }
            return GameIconService.TryLoadExecutableIcon(executablePath);
        }
        catch (Exception error)
        {
            AppLog.Error($"Standalone game icon load failed for '{executablePath}'", error);
            return null;
        }
    }

    /// <summary>
    /// Checks every known game's cached portrait once, after startup settles.
    /// Cards only check the games they show, and most detected games have no
    /// card, so their art otherwise never refreshed. Games with no cached
    /// portrait are skipped: this keeps art current, it does not fetch new art.
    /// </summary>
    public static async Task RefreshCachedAsync(IReadOnlyList<(string DetectionKey, string DisplayName)> games, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var refreshed = 0;
            foreach (var (detectionKey, displayName) in games
                         .Where(game => !string.IsNullOrWhiteSpace(game.DisplayName))
                         .DistinctBy(game => game.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(CachePathFor(displayName))) continue;
                await EditorForegroundWork.ParkWhileActiveAsync(cancellationToken).ConfigureAwait(false);
                if (await EnsureCachedAsync(detectionKey ?? string.Empty, displayName, cancellationToken).ConfigureAwait(false)) refreshed++;
            }
            if (refreshed > 0) AppLog.Info($"Game portraits: refreshed {refreshed} cached portrait(s).");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppLog.Error("Game portrait refresh sweep failed", error);
        }
    }

    /// <summary>
    /// Downloads the portrait if it is not cached yet, or replaces a cached one
    /// whose source has moved on. Returns true only when a new file was written,
    /// so callers can refresh exactly once instead of re-reading a bitmap they
    /// already have.
    /// </summary>
    public static Task<bool> EnsureCachedAsync(string detectionKey, string displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return Task.FromResult(false);
        // One check per name at a time, and every card asking shares its
        // answer. A second card used to get "nothing new" while the first one's
        // download ran: the Auto Clip and game-settings cards both ask for
        // Fortnite at startup, so one showed the new art and the other kept the old.
        lock (InFlight)
        {
            if (InFlight.TryGetValue(displayName, out var running)) return running;
            var task = EnsureCachedCoreAsync(detectionKey, displayName, cancellationToken);
            InFlight[displayName] = task;
            _ = task.ContinueWith(_ => { lock (InFlight) InFlight.Remove(displayName); }, CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            return task;
        }
    }

    private static async Task<bool> EnsureCachedCoreAsync(string detectionKey, string displayName, CancellationToken cancellationToken)
    {
        await Task.Yield();

        // A game that missed before can gain art the moment game-icons.json
        // grows an entry for it, and that file refreshes daily - so a miss
        // marker only holds off the name search, never a source known from the
        // game's own identity. Without this, adding curated art left everyone
        // who had already seen the game with no portrait for another week.
        var offlineUrl = await ResolveKnownPortraitUrlAsync(detectionKey, displayName, cancellationToken).ConfigureAwait(false);

        // A cached portrait used to be final. Fortnite's Epic art moved to its
        // season key art while the card kept the copy from a month before.
        var cached = new FileInfo(CachePathFor(displayName));
        string? refreshUrl = null;
        if (cached.Exists)
        {
            var recorded = ReadSource(displayName);
            // The legacy Steam URL is only a fallback for a failed store lookup:
            // never let it replace art recorded from a better source.
            var fallback = offlineUrl is not null && recorded is not null && offlineUrl.StartsWith(LegacySteamPrefix, StringComparison.Ordinal);
            if (offlineUrl is not null && !fallback && !string.Equals(offlineUrl, recorded, StringComparison.Ordinal)) refreshUrl = offlineUrl;
            else if (DateTime.UtcNow - cached.LastWriteTimeUtc > RefreshAfter)
            {
                // Art found by a Steam name search has no store entry to compare
                // with, so age is all there is: search again, falling back to the
                // URL it came from last time.
                string? searched = null;
                if (offlineUrl is null)
                {
                    try { searched = await SearchPortraitUrlAsync(displayName, cancellationToken).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException) { AppLog.Info($"Game portrait refresh search failed for '{displayName}': {error.Message}"); }
                }
                refreshUrl = offlineUrl ?? searched ?? recorded;
                // Nothing to refresh from: hold off another 30 days rather than
                // repeat the search on every launch.
                if (refreshUrl is null) TouchCached(cached);
            }
            if (refreshUrl is null) return false;
        }
        else if (offlineUrl is null && IsNegativeCacheFresh(displayName)) return false;

        // A failed refresh keeps the portrait already on screen and marks no
        // miss: the game has art, it just could not be updated this time.
        var refreshing = refreshUrl is not null;
        void Missed() { if (!refreshing) MarkMiss(displayName); }

        try
        {
            var url = refreshUrl ?? offlineUrl ?? await SearchPortraitUrlAsync(displayName, cancellationToken).ConfigureAwait(false);
            if (url is null)
            {
                Missed();
                return false;
            }

            Directory.CreateDirectory(CacheFolder);
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Missed();
                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 1024)
            {
                // Steam answers some missing art with a tiny placeholder rather
                // than a 404.
                Missed();
                return false;
            }

            // Written via a temp file so a cancelled or failed download can
            // never leave a truncated JPEG that every later load then fails on.
            var target = CachePathFor(displayName);
            var temp = target + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            // Written after the image, so a crash in between only costs one
            // more download next time rather than a source that lies.
            await File.WriteAllTextAsync(SourcePathFor(displayName), url, CancellationToken.None).ConfigureAwait(false);
            AppLog.Info($"Game portrait {(refreshing ? "refreshed" : "cached")}: '{displayName}'.");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error)
        {
            AppLog.Error($"Game portrait fetch failed for '{displayName}'", error);
            Missed();
            return false;
        }
    }

    // Order matters: the most certain source first, the name search last.
    // Everything here comes from the game's identity rather than a guess at its
    // name, which is what lets a miss marker be ignored for these.
    private static async Task<string?> ResolveKnownPortraitUrlAsync(string detectionKey, string displayName, CancellationToken cancellationToken)
    {
        // 1. Steam, straight off the detection key - no name matching, no ambiguity.
        var appId = ResolveAppId(detectionKey, displayName);
        if (appId is not null) return await SteamPortraitUrlAsync(appId.Value, cancellationToken).ConfigureAwait(false);

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
        return EpicPortraitUrl(displayName);
    }

    // 4. Anything else - a Battle.net or Riot title that also ships on Steam
    //    (Call of Duty, Diablo IV), or a game launched from its own exe - by
    //    searching Steam for the name. Plenty of games that ship on other
    //    launchers also have a Steam page, and this reuses the icon service's
    //    own name matching rather than a second guess at it.
    private static async Task<string?> SearchPortraitUrlAsync(string displayName, CancellationToken cancellationToken)
    {
        var searched = await GameIconService.ResolveSteamAppIdForAsync(displayName, cancellationToken).ConfigureAwait(false);
        return searched is > 0 ? await SteamPortraitUrlAsync(searched.Value, cancellationToken).ConfigureAwait(false) : null;
    }

    // library_600x900 at the fixed per-app URL is Steam's old portrait path,
    // frozen when Steam moved store art to hashed paths: Overwatch's still
    // served the "Overwatch 2" art from February 2024 after the game was
    // renamed and repainted. Kept as the fallback when the store API fails;
    // games predating portrait art answer 404, which the caller treats as a miss.
    private const string LegacySteamPrefix = "https://cdn.cloudflare.steamstatic.com/steam/apps/";
    private static string LegacySteamPortraitUrl(int appId) => $"{LegacySteamPrefix}{appId}/library_600x900.jpg";

    // Asked once per app per session; several cards and the startup sweep
    // all want the same answer.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> SteamPortraitUrls = new();

    // The store's own record of the current portrait. Its path carries a hash
    // that changes with the art, so a recorded source that differs means new
    // art. The ?t= stamp is left off: it moves when ANY store asset changes.
    private static async Task<string> SteamPortraitUrlAsync(int appId, CancellationToken cancellationToken)
    {
        if (SteamPortraitUrls.TryGetValue(appId, out var known)) return known;
        var url = LegacySteamPortraitUrl(appId);
        try
        {
            var input = $"{{\"ids\":[{{\"appid\":{appId}}}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\"}},\"data_request\":{{\"include_assets\":true}}}}";
            var json = await Http.GetStringAsync("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input), cancellationToken).ConfigureAwait(false);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("response", out var response)
                && response.TryGetProperty("store_items", out var items) && items.GetArrayLength() > 0
                && items[0].TryGetProperty("assets", out var assets)
                && assets.TryGetProperty("asset_url_format", out var format)
                && (assets.TryGetProperty("library_capsule_2x", out var file) || assets.TryGetProperty("library_capsule", out file))
                && format.GetString() is { } pattern && file.GetString() is { Length: > 0 } name)
            {
                var path = pattern.Replace("${FILENAME}", name, StringComparison.Ordinal);
                var query = path.IndexOf('?');
                url = "https://shared.steamstatic.com/store_item_assets/" + (query >= 0 ? path[..query] : path);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AppLog.Info($"Steam portrait lookup failed for app {appId}; using the legacy URL: {error.Message}");
            return url;
        }
        SteamPortraitUrls[appId] = url;
        return url;
    }

    // %ProgramData%\Epic\EpicGamesLauncher\Data\Catalog\catcache.bin is
    // base64-encoded JSON: every catalogue entry the launcher knows about, each
    // with a title and a keyImages list. DieselGameBoxTall is the 1200x1600
    // portrait. Matched on the exact title because a Fortnite library also
    // contains "Fortnite Crew", "2800 Fortnite Points" and a dozen other
    // entries a fuzzy match would happily return instead.
    private static string? EpicPortraitUrl(string displayName) =>
        EpicPortraits().TryGetValue(displayName, out var url) ? url : null;

    // Parsed once and kept, re-read only when the launcher rewrites the file.
    // The catalogue is a 350KB base64 blob holding a few hundred entries, and
    // decoding plus parsing it per lookup was most of the ~1.6s an Epic game's
    // portrait took to appear - the download itself is a fraction of that.
    private static readonly object EpicSync = new();
    private static Dictionary<string, string>? _epicPortraits;
    private static DateTime _epicStampUtc;

    private static Dictionary<string, string> EpicPortraits()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return _epicPortraits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            lock (EpicSync)
            {
                if (_epicPortraits is not null && file.LastWriteTimeUtc == _epicStampUtc) return _epicPortraits;

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var json = Convert.FromBase64String(File.ReadAllText(path));
                using var document = System.Text.Json.JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var entry in document.RootElement.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("title", out var titleElement)) continue;
                        var title = titleElement.GetString();
                        if (string.IsNullOrWhiteSpace(title) || map.ContainsKey(title)) continue;
                        if (!entry.TryGetProperty("keyImages", out var images)) continue;

                        foreach (var image in images.EnumerateArray())
                        {
                            if (!image.TryGetProperty("type", out var typeElement)) continue;
                            if (!string.Equals(typeElement.GetString(), "DieselGameBoxTall", StringComparison.Ordinal)) continue;
                            if (!image.TryGetProperty("url", out var urlElement)) continue;
                            var url = urlElement.GetString();
                            if (!string.IsNullOrWhiteSpace(url)) map[title] = url;
                            break;
                        }
                    }
                }

                _epicStampUtc = file.LastWriteTimeUtc;
                return _epicPortraits = map;
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Epic portrait catalogue parse failed", error);
            return _epicPortraits ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

    private static void TouchCached(FileInfo cached)
    {
        try { cached.LastWriteTimeUtc = DateTime.UtcNow; }
        catch { /* only postpones the next check */ }
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
