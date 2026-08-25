using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Win32;

namespace ClypDat.App.Services;

/// <summary>
/// Real per-game icons for the Library sidebar rail, pulled from the game's
/// own executable rather than shipped as assets - there's no bundled artwork
/// for an arbitrary game, and the exe icon is what the user already
/// associates with it from the taskbar.
///
/// Icons can only be extracted while the game is actually running (that's the
/// only time its executable path is known), so this caches to disk on
/// detection and every later lookup is a cache read. A game that has never
/// been seen running just has no icon and falls back to its initial badge.
/// </summary>
public static class GameIconService
{
    private static readonly string CacheFolder = Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "game-icons");

    // Extraction is best-effort and never worth repeating in a session once
    // it has failed (protected process, no icon resource, access denied).
    private static readonly HashSet<string> Attempted = new(StringComparer.OrdinalIgnoreCase);

    // One instance for the process rather than one per lookup. The old
    // per-call `using var client = new HttpClient` churned sockets, and with
    // no negative cache below it was doing so several times a minute for a
    // name the store was never going to resolve.
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-GameIcons/1.0 (+https://github.com/ClypLabs/ClypDat)");
        return client;
    }

    // How long a name that resolved to nothing online stays suppressed.
    //
    // NetworkAttempted below deliberately un-marks a failed lookup so a later
    // sweep can retry it - that exists so a game added to the curated
    // game-icons.json list after a first failed try still picks an icon up.
    // But every library change triggers a sweep, so in practice a name with no
    // artwork anywhere (a plain "Desktop Capture" recording, say) re-ran a
    // Steam store search every time: real logs show eight attempts in ten
    // minutes. Persisting the miss keeps the eventual-retry behaviour and drops
    // the cost of it to a File.Exists.
    private static readonly TimeSpan NegativeCacheRetryAfter = TimeSpan.FromDays(1);

    private static string CachePathFor(string displayName) => Path.Combine(CacheFolder, $"{SafeFileName(displayName)}.png");
    private static string CuratedSourcePathFor(string displayName) => Path.Combine(CacheFolder, $"{SafeFileName(displayName)}.source");
    private static string NegativeMarkerPathFor(string displayName) => Path.Combine(CacheFolder, $"{SafeFileName(displayName)}.miss");

    private static string SafeFileName(string displayName) =>
        string.Join("_", displayName.Split(Path.GetInvalidFileNameChars()));

    // The marker's own last-write time is the timestamp - no content to parse,
    // and a manual delete of the file is a valid way to force a retry.
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

    private static void MarkNegative(string displayName)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var path = NegativeMarkerPathFor(displayName);
            File.WriteAllBytes(path, Array.Empty<byte>());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // Best-effort: without the marker this just behaves as it did
            // before, retrying on the next sweep.
        }
    }

    private static void ClearNegative(string displayName)
    {
        try
        {
            var path = NegativeMarkerPathFor(displayName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    // Names are compared with punctuation, casing, trademark symbols and
    // edition suffixes stripped, so "Counter-Strike 2" matches "Counter-Strike 2"
    // but "Overwatch 2" does NOT match "Overwatch(R) Starter Pack 2026: Season 3".
    // Without that guard a store search happily returns DLC or a soundtrack and
    // the sidebar ends up showing art for the wrong product.
    private static string NormalizeName(string name)
    {
        var chars = name
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static readonly HashSet<string> NetworkAttempted = new(StringComparer.OrdinalIgnoreCase);

    // Icon URLs are fetched and decoded without the user ever seeing them, and
    // one of the sources (game-icons.json) is editable outside a release. Rather
    // than trust whatever string comes back, only fetch HTTPS from the hosts
    // that legitimately serve this artwork. Anything else is refused and logged.
    private static readonly string[] AllowedIconHostSuffixes =
    {
        "steamstatic.com",
        "steampowered.com",
        "githubusercontent.com"
    };

    internal static bool IsAllowedIconUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;
        return AllowedIconHostSuffixes.Any(suffix =>
            host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves an icon for a game from the internet, so a game only ever
    /// clipped (never seen running by this install) still gets real artwork.
    /// Tried in order: curated icon URL, Steam library cache, existing cache,
    /// installed copy, curated Steam app ID, then a Steam store search by name.
    /// Entirely automatic - the curated entries exist only for games the store
    /// search can't resolve (not on Steam, delisted, or a different name).
    /// </summary>
    public static async Task<bool> EnsureFromNetworkAsync(string displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        lock (NetworkAttempted)
        {
            if (!NetworkAttempted.Add(displayName)) return false;
        }

        // A failed lookup (network hiccup, store search came up empty, curated
        // data not covering it yet) used to stay marked attempted for the rest
        // of the running process, same as a successful one - so a game that
        // missed on its first try (or was only added to game-icons.json's
        // curated list after that first try already failed) could never pick
        // up an icon again without a full manual "Refresh game icons". Only a
        // genuine success should stick; anything else clears the marker so
        // the next missing-icon sweep (any library change) retries it.
        var succeeded = false;
        try
        {
            // Steam has already downloaded the exact small square icon it
            // displays in its own client. Prefer it over any web asset: some
            // recent games publish a stale community icon hash, while the
            // local library cache is the artwork the player actually sees.
            await RemoteGameIconsService.EnsureLoadedAsync(cancellationToken);

            var curatedUrl = RemoteGameIconsService.LoadCached()
                .FirstOrDefault(pair => string.Equals(pair.Key, displayName, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(curatedUrl) && IsAllowedIconUrl(curatedUrl))
            {
                var sourcePath = CuratedSourcePathFor(displayName);
                var currentSource = File.Exists(sourcePath) ? File.ReadAllText(sourcePath) : null;
                if (!File.Exists(CachePathFor(displayName)) || !string.Equals(currentSource, curatedUrl, StringComparison.Ordinal))
                {
                    try
                    {
                        var curatedBytes = await Http.GetByteArrayAsync(curatedUrl, cancellationToken);
                        using var curatedStream = new MemoryStream(curatedBytes);
                        using var curatedBitmap = new Bitmap(curatedStream);
                        Directory.CreateDirectory(CacheFolder);
                        curatedBitmap.Save(CachePathFor(displayName), PngBitmapEncoderOptions.Default);
                        File.WriteAllText(sourcePath, curatedUrl);
                        AppLog.Info($"Curated game icon refreshed for '{displayName}' from {curatedUrl}.");
                        succeeded = true;
                        return true;
                    }
                    catch (Exception error)
                    {
                        AppLog.Error($"Curated game icon refresh failed for '{displayName}' (non-fatal)", error);
                    }
                }
                else
                {
                    succeeded = true;
                    return false;
                }
            }

            if (TryCacheSteamLibraryIcon(displayName))
            {
                succeeded = true;
                return true;
            }

            var cachePath = CachePathFor(displayName);
            if (File.Exists(cachePath))
            {
                succeeded = true;
                return false;
            }

            // An installed copy is the best source there is - it's the exact
            // icon Windows shows for the game - and it's the only one that
            // covers Epic, Battle.net, EA, GOG and the rest, which no amount of
            // Steam lookups will ever find.
            cancellationToken.ThrowIfCancellationRequested();
            var installedExecutable = InstalledGameLocator.FindExecutable(displayName, cancellationToken);
            if (installedExecutable is not null)
            {
                var installedIcon = ExtractIconBitmap(installedExecutable);
                if (installedIcon is not null)
                {
                    Directory.CreateDirectory(CacheFolder);
                    using (installedIcon)
                    {
                        installedIcon.Save(cachePath, PngBitmapEncoderOptions.Default);
                    }

                    AppLog.Info($"Game icon taken from installed game for '{displayName}': {installedExecutable}.");
                    succeeded = true;
                    return true;
                }
            }

            // Local sources above are cheap and can newly succeed (the game was
            // just installed), so they are tried every sweep. The online search
            // is the expensive part and the one worth suppressing.
            if (IsNegativeCacheFresh(displayName)) return false;

            var url = await ResolveIconUrlAsync(Http, displayName, cancellationToken);
            if (string.IsNullOrWhiteSpace(url))
            {
                MarkNegative(displayName);
                AppLog.Info($"Game icon: nothing resolved online for '{displayName}'.");
                return false;
            }

            if (!IsAllowedIconUrl(url))
            {
                AppLog.Error($"Game icon URL rejected for '{displayName}': {url}", new InvalidOperationException("Icon URL is not an allowed HTTPS source."));
                return false;
            }

            var bytes = await Http.GetByteArrayAsync(url, cancellationToken);
            using var stream = new MemoryStream(bytes);
            using var bitmap = new Bitmap(stream);

            Directory.CreateDirectory(CacheFolder);
            bitmap.Save(CachePathFor(displayName), PngBitmapEncoderOptions.Default);
            AppLog.Info($"Game icon fetched for '{displayName}' from {url}.");
            succeeded = true;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            AppLog.Error($"Game icon fetch failed for '{displayName}' (non-fatal)", error);
            return false;
        }
        finally
        {
            if (succeeded) ClearNegative(displayName);
            else lock (NetworkAttempted) NetworkAttempted.Remove(displayName);
        }
    }

    // A curated URL wins outright (it's there because the automatic sources got
    // this game wrong), then a curated app ID, then a name search on the store.
    private static async Task<string?> ResolveIconUrlAsync(HttpClient client, string displayName, CancellationToken cancellationToken)
    {
        // Both curated maps are read below, so the list has to have actually
        // arrived first - see EnsureLoadedAsync for what reading it too early
        // used to cost.
        await RemoteGameIconsService.EnsureLoadedAsync(cancellationToken);

        if (RemoteGameIconsService.LoadCached().TryGetValue(displayName, out var curated) && !string.IsNullOrWhiteSpace(curated))
        {
            return curated;
        }

        if (RemoteGameIconsService.LoadCachedAppIds().TryGetValue(displayName, out var curatedAppId) && curatedAppId > 0)
        {
            var curatedIcon = await ResolveSteamIconUrlAsync(client, curatedAppId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(curatedIcon)) return curatedIcon;
        }

        return await ResolveSteamAppIconAsync(client, displayName, cancellationToken);
    }

    // Steam's own app icon - the same square artwork Steam puts on a desktop
    // shortcut and the taskbar, which is what a small round badge wants.
    //
    // Two keyless calls: the store search resolves a name to an appid, then
    // ICommunityService/GetApps gives that app's icon hash, which addresses the
    // icon on Steam's community CDN.
    private static async Task<string?> ResolveSteamAppIconAsync(HttpClient client, string displayName, CancellationToken cancellationToken)
    {
        try
        {
            var appId = await ResolveSteamAppIdAsync(client, displayName, cancellationToken);
            return appId is null ? null : await ResolveSteamIconUrlAsync(client, appId.Value, cancellationToken);
        }
        catch (Exception error)
        {
            AppLog.Error($"Steam icon lookup failed for '{displayName}' (non-fatal)", error);
            return null;
        }
    }

    private static async Task<string?> ResolveSteamIconUrlAsync(HttpClient client, int appId, CancellationToken cancellationToken)
    {
        var json = await client.GetStringAsync(
            $"https://api.steampowered.com/ICommunityService/GetApps/v1/?appids%5B0%5D={appId}",
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out var response)) return null;
        if (!response.TryGetProperty("apps", out var apps)) return null;

        foreach (var app in apps.EnumerateArray())
        {
            if (!app.TryGetProperty("icon", out var iconElement)) continue;
            var hash = iconElement.GetString();
            if (string.IsNullOrWhiteSpace(hash)) continue;

            // Steam serves most established games from its original JPG path,
            // while newer games such as Umamusume only expose the current ICO
            // path. Check both paths instead of choosing one globally.
            var legacyUrl = $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{hash}.jpg";
            if (await IsAvailableAsync(client, legacyUrl, cancellationToken)) return legacyUrl;

            var communityUrl = $"https://shared.fastly.steamstatic.com/community_assets/images/apps/{appId}/{hash}.ico";
            if (await IsAvailableAsync(client, communityUrl, cancellationToken)) return communityUrl;
        }

        return null;
    }

    private static async Task<bool> IsAvailableAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static bool TryCacheSteamLibraryIcon(string displayName)
    {
        try
        {
            if (!RemoteGameIconsService.LoadCachedAppIds().TryGetValue(displayName, out var appId) || appId <= 0) return false;

            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steamPath = steamKey?.GetValue("SteamPath") as string;
            if (string.IsNullOrWhiteSpace(steamPath)) return false;

            var iconFolder = Path.Combine(steamPath, "appcache", "librarycache", appId.ToString());
            var iconPath = Directory.Exists(iconFolder)
                ? Directory.EnumerateFiles(iconFolder, "*.jpg", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;
            if (iconPath is null) return false;

            using var icon = new Bitmap(iconPath);
            Directory.CreateDirectory(CacheFolder);
            icon.Save(CachePathFor(displayName), PngBitmapEncoderOptions.Default);
            AppLog.Info($"Game icon copied from Steam library cache for '{displayName}'.");
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"Steam library icon lookup failed for '{displayName}' (non-fatal)", error);
            return false;
        }
    }

    // Words that mark a store entry as something other than the game itself.
    // Checked against the part of a candidate's name that the game's own name
    // doesn't account for, so "Rocket League" is unaffected by "League" here.
    private static readonly string[] NonGameNameMarkers =
    {
        "soundtrack", "ost", "dlc", "pack", "bundle", "edition upgrade", "upgrade",
        "season pass", "expansion", "demo", "beta", "test", "server", "sdk",
        "artbook", "art book", "wallpaper", "skin", "currency", "coins", "credits",
        "starter", "membership", "subscription", "highresolution", "high resolution"
    };

    /// <summary>
    /// The name to search for, then progressively shorter leading parts of it.
    /// The store search is a text match on Steam's side, so a name that has
    /// drifted from the store's returns NOTHING at all and no amount of
    /// filtering the results helps: "Splitgate: Arena Warfare" is empty,
    /// because Steam now calls it "SPLITGATE: Arena Reloaded", while
    /// "Splitgate: Arena" finds it immediately.
    ///
    /// Dropping trailing words stops at two, unless the first word is long
    /// enough to stand on its own (a "Splitgate" or an "Umamusume") - a
    /// one-word search on something like "Rocket" would happily return a
    /// completely different game.
    /// </summary>
    private static IEnumerable<string> SearchTermsFor(string displayName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { displayName };
        yield return displayName;

        var words = displayName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var take = words.Length - 1; take >= 1; take--)
        {
            var term = string.Join(' ', words.Take(take)).Trim(':', '-', '–', ',', ' ');
            if (term.Length == 0) continue;
            if (take == 1 && term.Length < 8) yield break;
            if (seen.Add(term)) yield return term;
        }
    }

    private static async Task<int?> ResolveSteamAppIdAsync(HttpClient client, string displayName, CancellationToken cancellationToken)
    {
        var first = true;
        foreach (var term in SearchTermsFor(displayName))
        {
            // Exact-match-wins is only trustworthy against the name actually
            // being looked up, not a fragment of it we chose to try after the
            // full name came up empty. Searching "Counter-Strike" (the
            // trimmed-down fallback for "Counter-Strike: Global Offensive",
            // since CS:GO itself no longer exists as a separate store
            // listing - Valve renamed the same appid to Counter-Strike 2)
            // finds THE ORIGINAL 1999 GAME as an exact name match - a real,
            // separate, correctly-named product that just happens to share
            // the franchise's base name. Once a term is a shortened fallback,
            // only genuine prefix/suffix matching is trusted; a fallback term
            // matching something exactly is coincidence, not confirmation.
            var id = await SearchSteamAppIdAsync(client, term, cancellationToken, allowExactMatch: first);
            first = false;
            if (id is null) continue;
            if (!string.Equals(term, displayName, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Info($"Steam icon lookup: '{displayName}' matched by searching '{term}' (appid {id}).");
            }

            return id;
        }

        return null;
    }

    // Matches candidates against the term that was actually searched for, not
    // the original name - the whole point of a shortened term is that the full
    // name no longer corresponds to anything in the store.
    private static async Task<int?> SearchSteamAppIdAsync(HttpClient client, string displayName, CancellationToken cancellationToken, bool allowExactMatch)
    {
        var json = await client.GetStringAsync(
            $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(displayName)}&cc=us&l=en",
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items)) return null;

        var wanted = NormalizeName(displayName);
        int? prefixMatch = null;

        foreach (var item in items.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) continue;

            var candidate = NormalizeName(name);
            if (candidate.Length == 0) continue;

            // Exact match still wins outright, whatever its position - but
            // only on the real name, see ResolveSteamAppIdAsync's comment.
            if (allowExactMatch && string.Equals(candidate, wanted, StringComparison.Ordinal)) return id;

            // Otherwise take the best-ranked candidate that is the same title
            // under a longer or shorter name - "Umamusume" against "Umamusume:
            // Pretty Derby", "Rainbow Six Siege" against "Tom Clancy's Rainbow
            // Six Siege". Requiring exact equality meant every game whose
            // store name carries a subtitle or publisher prefix resolved to
            // nothing at all, which is most of them.
            if (prefixMatch is not null) continue;
            if (!candidate.StartsWith(wanted, StringComparison.Ordinal) &&
                !wanted.StartsWith(candidate, StringComparison.Ordinal) &&
                !candidate.EndsWith(wanted, StringComparison.Ordinal))
            {
                continue;
            }

            // The extra words are what decide whether this is the game or
            // merchandise attached to it.
            var extra = candidate.Length > wanted.Length ? name : displayName;
            if (NonGameNameMarkers.Any(marker => extra.Contains(marker, StringComparison.OrdinalIgnoreCase))) continue;

            prefixMatch = id;
        }

        return prefixMatch;
    }

    /// <summary>
    /// Throws away every cached icon and the per-session "already tried this
    /// one" marks, so the next lookup starts from nothing. The manual escape
    /// hatch for the cache having no expiry: an icon that resolved to the
    /// wrong artwork, or to a launcher's logo instead of the game's, would
    /// otherwise stay wrong for good. Returns how many cached images went.
    /// </summary>
    public static int ClearCache()
    {
        lock (Attempted) Attempted.Clear();
        lock (NetworkAttempted) NetworkAttempted.Clear();

        var removed = 0;
        try
        {
            if (!Directory.Exists(CacheFolder)) return 0;
            foreach (var file in Directory.EnumerateFiles(CacheFolder, "*.png"))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception error)
                {
                    // A file still open somewhere (the icon is loaded into a
                    // live Bitmap) just stays put and gets reused.
                    AppLog.Error($"Game icon cache: could not delete {file}", error);
                }
            }

            // Negative markers too, or an explicit refresh would silently skip
            // every name that missed in the last day - the exact case the user
            // is reaching for this button to fix. Not counted in `removed`,
            // which reports cached images.
            foreach (var marker in Directory.EnumerateFiles(CacheFolder, "*.miss"))
            {
                try { File.Delete(marker); } catch { }
            }

            foreach (var marker in Directory.EnumerateFiles(CacheFolder, "*.source"))
            {
                try { File.Delete(marker); } catch { }
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Game icon cache clear failed", error);
        }

        AppLog.Info($"Game icon cache cleared: {removed} images removed.");
        return removed;
    }

    /// <summary>
    /// Carries a cached icon over to a new display name (a user rename). The
    /// cache is keyed by name, so without this a renamed game loses its
    /// artwork and has to resolve again under a name a store search is even
    /// less likely to recognise. Leaves an existing icon at the destination
    /// alone.
    /// </summary>
    public static void CopyCachedIcon(string fromDisplayName, string toDisplayName)
    {
        if (string.IsNullOrWhiteSpace(fromDisplayName) || string.IsNullOrWhiteSpace(toDisplayName)) return;
        try
        {
            var source = CachePathFor(fromDisplayName);
            var destination = CachePathFor(toDisplayName);
            if (!File.Exists(source) || File.Exists(destination)) return;
            Directory.CreateDirectory(CacheFolder);
            File.Copy(source, destination);
            var sourceMarker = CuratedSourcePathFor(fromDisplayName);
            if (File.Exists(sourceMarker)) File.Copy(sourceMarker, CuratedSourcePathFor(toDisplayName), overwrite: true);
        }
        catch (Exception error)
        {
            AppLog.Error($"Game icon copy failed for '{fromDisplayName}' -> '{toDisplayName}'", error);
        }
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
            AppLog.Error($"Game icon load failed for '{displayName}'", error);
            return null;
        }
    }

    /// <summary>
    /// Extracts and caches the icon for a detected game if it isn't cached
    /// yet. Safe to call on every detection tick - it short-circuits once the
    /// icon exists or extraction has already been tried this session.
    /// Returns true only when a new icon was written.
    /// </summary>
    public static bool EnsureCached(string displayName, int processId)
    {
        if (string.IsNullOrWhiteSpace(displayName) || processId <= 0) return false;
        lock (Attempted)
        {
            if (!Attempted.Add(displayName)) return false;
        }

        try
        {
            var cachePath = CachePathFor(displayName);
            if (File.Exists(cachePath)) return false;

            var exePath = ResolveExecutablePath(processId);
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                AppLog.Info($"Game icon: no executable path resolved for '{displayName}' (pid={processId}).");
                return false;
            }

            var bitmap = ExtractIconBitmap(exePath);
            if (bitmap is null)
            {
                AppLog.Info($"Game icon: no icon extracted for '{displayName}' from {exePath}.");
                return false;
            }

            Directory.CreateDirectory(CacheFolder);
            using (bitmap)
            {
                bitmap.Save(cachePath, PngBitmapEncoderOptions.Default);
            }

            AppLog.Info($"Game icon cached for '{displayName}' from {exePath}.");
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"Game icon extraction failed for '{displayName}'", error);
            return false;
        }
    }

    // Process.MainModule throws for anything running at a higher integrity
    // level than us, which plenty of games do - QueryFullProcessImageName
    // against a limited-rights handle works where MainModule doesn't.
    private static string? ResolveExecutablePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == nint.Zero)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // Reads the exe's large icon into an Avalonia bitmap. Goes through the
    // icon's own 32bpp colour bitmap so alpha survives - drawing an HICON
    // into a DC and reading that back loses transparency.
    private static Bitmap? ExtractIconBitmap(string exePath)
    {
        var large = new nint[1];
        var small = new nint[1];
        if (ExtractIconEx(exePath, 0, large, small, 1) <= 0) return null;

        var icon = large[0] != nint.Zero ? large[0] : small[0];
        if (icon == nint.Zero) return null;

        try
        {
            if (!GetIconInfo(icon, out var info))
            {
                AppLog.Error($"Game icon: GetIconInfo failed for {exePath}", new InvalidOperationException($"win32={Marshal.GetLastWin32Error()}"));
                return null;
            }

            try
            {
                if (info.hbmColor == nint.Zero) return null;

                // Dimensions come from the bitmap handle itself rather than a
                // header-query pass of GetDIBits, which only reports them when
                // called with biBitCount zeroed and is easy to get subtly wrong.
                if (GetObject(info.hbmColor, Marshal.SizeOf<Win32Bitmap>(), out var bitmapInfo) == 0) return null;

                var width = bitmapInfo.bmWidth;
                var height = bitmapInfo.bmHeight;
                if (width <= 0 || height <= 0) return null;

                var header = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = width,
                    // Negative height requests top-down rows, matching the
                    // order Avalonia expects.
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                    biSizeImage = (uint)(width * height * 4)
                };

                var screenDc = GetDC(nint.Zero);
                if (screenDc == nint.Zero) return null;

                try
                {
                    var bgra = ReadBitmapPixels(screenDc, info.hbmColor, width, height, ref header);
                    if (bgra is null)
                    {
                        AppLog.Error($"Game icon: GetDIBits failed for {exePath}", new InvalidOperationException($"win32={Marshal.GetLastWin32Error()}"));
                        return null;
                    }

                    ApplyMaskAlphaIfNeeded(screenDc, info.hbmMask, bgra, width, height, ref header);

                    var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                    try
                    {
                        return new Bitmap(
                            PixelFormat.Bgra8888,
                            AlphaFormat.Unpremul,
                            handle.AddrOfPinnedObject(),
                            new PixelSize(width, height),
                            new Vector(96, 96),
                            width * 4);
                    }
                    finally
                    {
                        handle.Free();
                    }
                }
                finally
                {
                    ReleaseDC(nint.Zero, screenDc);
                }
            }
            finally
            {
                if (info.hbmColor != nint.Zero) DeleteObject(info.hbmColor);
                if (info.hbmMask != nint.Zero) DeleteObject(info.hbmMask);
            }
        }
        finally
        {
            if (large[0] != nint.Zero) DestroyIcon(large[0]);
            if (small[0] != nint.Zero && small[0] != large[0]) DestroyIcon(small[0]);
        }
    }

    private static byte[]? ReadBitmapPixels(nint dc, nint bitmap, int width, int height, ref BitmapInfoHeader header)
    {
        var buffer = new byte[width * height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return GetDIBits(dc, bitmap, 0, (uint)height, handle.AddrOfPinnedObject(), ref header, 0) == 0 ? null : buffer;
        }
        finally
        {
            handle.Free();
        }
    }

    // GDI routinely hands back icon colour bits with the alpha channel zeroed,
    // which would render the whole icon invisible. When that happens the
    // icon's AND mask is the real transparency source: black means opaque,
    // white means see-through. Only applied when every pixel came back fully
    // transparent, so genuinely alpha-blended icons are left untouched.
    private static void ApplyMaskAlphaIfNeeded(nint dc, nint maskBitmap, byte[] bgra, int width, int height, ref BitmapInfoHeader header)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0) return;
        }

        if (maskBitmap == nint.Zero)
        {
            // No mask to consult - a fully opaque icon beats an invisible one.
            for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
            return;
        }

        var mask = ReadBitmapPixels(dc, maskBitmap, width, height, ref header);
        if (mask is null)
        {
            for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
            return;
        }

        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, System.Text.StringBuilder exeName, ref int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ExtractIconEx(string file, int iconIndex, nint[] largeIcons, nint[] smallIcons, int icons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(nint dc, nint bitmap, uint startScan, uint scanLines, nint bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(nint handle, int size, out Win32Bitmap bitmap);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Bitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        // GetDIBits writes a colour table past the header for <=8bpp formats;
        // reserving it here keeps it from scribbling past the struct.
        public uint biColorTable0;
        public uint biColorTable1;
        public uint biColorTable2;
    }
}
