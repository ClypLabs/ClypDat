using System.Net.Http;
using System.Text.Json;

namespace ClypDat.App.Services;

// Curated game-name -> icon-URL overrides, hosted in the repo
// (native/game-icons.json) and fetched at runtime, so a game can be covered
// for every user by editing one file on master instead of shipping a release.
// Exactly the same mechanism (and reasoning) as RemoteGameExclusionsService.
//
// This only has to carry games GameIconService can't resolve on its own -
// it searches the Steam store by name first, so this is for titles that
// aren't on Steam at all, or whose name matches the wrong Steam product.
// A fetch failure just means those specific games fall back to their
// executable icon or a letter badge.
public static class RemoteGameIconsService
{
    private const string IconsUrl = "https://raw.githubusercontent.com/ClypDat/ClypDat/master/native/game-icons.json";
    private const string CacheFileName = "remote-game-icons.json";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    // game-icons.json is hand-edited and uses lowercase keys ("icons"), which
    // the serializer's default case-sensitive matching would silently ignore,
    // leaving the curated list permanently empty.
    private static readonly JsonSerializerOptions DocumentOptions = new() { PropertyNameCaseInsensitive = true };

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClypDat", CacheFileName);

    private static Dictionary<string, string>? _memoryCache;
    private static Dictionary<string, int>? _appIdMemoryCache;

    // Synchronous and network-free, so icon resolution can consult the curated
    // list immediately at startup rather than waiting on RefreshAsync.
    public static IReadOnlyDictionary<string, string> LoadCached()
    {
        if (_memoryCache is not null) return _memoryCache;
        _memoryCache = TryReadCacheEntry()?.Icons ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return _memoryCache;
    }

    // Curated Steam app IDs, for titles the store search can't resolve by name:
    // delisted games (Rocket League still has its Steam app and CDN icon, but
    // no longer appears in search) and games whose store name doesn't match the
    // one shown on a clip ("Rainbow Six Siege" vs "Tom Clancy's Rainbow Six
    // Siege"). Cheaper and safer than a curated URL - the icon still comes from
    // Steam's own CDN, so it needs no new allowed host.
    public static IReadOnlyDictionary<string, int> LoadCachedAppIds()
    {
        if (_appIdMemoryCache is not null) return _appIdMemoryCache;
        _appIdMemoryCache = TryReadCacheEntry()?.SteamAppIds ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return _appIdMemoryCache;
    }

    public static async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cached = TryReadCacheEntry();
            if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < RefreshInterval) return;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-GameIcons");
            var json = await client.GetStringAsync(IconsUrl, cancellationToken);

            var document = JsonSerializer.Deserialize<IconsDocument>(json, DocumentOptions);
            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in document?.Icons ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)) icons[pair.Key] = pair.Value;
            }

            var appIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in document?.SteamAppIds ?? new Dictionary<string, int>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0) appIds[pair.Key] = pair.Value;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheEntry(DateTimeOffset.UtcNow, icons, appIds)));
            _memoryCache = icons;
            _appIdMemoryCache = appIds;
        }
        catch (Exception error)
        {
            AppLog.Error("Remote game-icons refresh failed (non-fatal)", error);
        }
    }

    private static CacheEntry? TryReadCacheEntry()
    {
        try
        {
            return File.Exists(CachePath) ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(CachePath)) : null;
        }
        catch
        {
            return null;
        }
    }

    // SteamAppIds is absent from cache files written before it existed, so it
    // stays nullable and callers fall back to an empty map.
    private sealed record CacheEntry(DateTimeOffset FetchedAt, Dictionary<string, string> Icons, Dictionary<string, int>? SteamAppIds = null);

    private sealed class IconsDocument
    {
        public Dictionary<string, string>? Icons { get; set; }
        public Dictionary<string, int>? SteamAppIds { get; set; }
    }
}
