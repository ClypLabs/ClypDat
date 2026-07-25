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

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClypDat", CacheFileName);

    private static Dictionary<string, string>? _memoryCache;

    // Synchronous and network-free, so icon resolution can consult the curated
    // list immediately at startup rather than waiting on RefreshAsync.
    public static IReadOnlyDictionary<string, string> LoadCached()
    {
        if (_memoryCache is not null) return _memoryCache;
        _memoryCache = TryReadCacheEntry()?.Icons ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return _memoryCache;
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

            var document = JsonSerializer.Deserialize<IconsDocument>(json);
            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in document?.Icons ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)) icons[pair.Key] = pair.Value;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheEntry(DateTimeOffset.UtcNow, icons)));
            _memoryCache = icons;
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

    private sealed record CacheEntry(DateTimeOffset FetchedAt, Dictionary<string, string> Icons);

    private sealed class IconsDocument
    {
        public Dictionary<string, string>? Icons { get; set; }
    }
}
