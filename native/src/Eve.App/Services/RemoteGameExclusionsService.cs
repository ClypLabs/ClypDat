using System.Net.Http;
using System.Text.Json;

namespace Eve.App.Services;

// Community-maintained list of known non-game executables (chat apps,
// overlays, browsers, etc.) that get mistaken for a game - hosted in the repo
// (native/game-detection-exclusions.json) instead of only baked into
// ForegroundGameDetector.IgnoredExecutables, so a newly-discovered false
// positive can be fixed for every user by editing one file on GitHub instead
// of shipping a new release. Purely additive: a fetch failure, malformed
// upstream file, or first run with no cache yet just means nothing extra this
// session - the built-in list (and the user's own exclusions) are unaffected
// either way.
public static class RemoteGameExclusionsService
{
    private const string ExclusionsUrl = "https://raw.githubusercontent.com/Stormanzanii/EVE/master/native/game-detection-exclusions.json";
    private const string CacheFileName = "remote-game-exclusions.json";

    // Refetching every launch would hammer raw.githubusercontent.com for no
    // real benefit - a false positive fixed upstream doesn't need to land
    // within minutes, so a day between checks is plenty responsive while
    // being a reasonable citizen of a free CDN.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EVE", CacheFileName);

    // Fast, synchronous, no network - whatever was cached from the last
    // successful fetch (if any), so detection has the community list from the
    // moment the app starts instead of waiting on RefreshAsync's own result.
    public static IReadOnlyList<string> LoadCached()
    {
        var cached = TryReadCacheEntry();
        return cached?.Executables ?? Array.Empty<string>();
    }

    // Best-effort background refresh, throttled to once per RefreshInterval via
    // the cache file's own FetchedAt - safe to call on every app startup
    // without actually hitting the network every time. Returns null when
    // nothing new was fetched (still within the throttle window, or the fetch
    // failed) - caller keeps using whatever LoadCached already returned.
    public static async Task<IReadOnlyList<string>?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cached = TryReadCacheEntry();
            if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < RefreshInterval)
            {
                return null;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EVE-GameDetector");
            var json = await client.GetStringAsync(ExclusionsUrl, cancellationToken);
            var names = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            var cleaned = names.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheEntry(DateTimeOffset.UtcNow, cleaned)));
            return cleaned;
        }
        catch (Exception error)
        {
            // Network hiccup, GitHub unreachable, malformed file upstream - the
            // built-in and previously-cached lists are still in effect, so
            // this is never fatal to detection, just logged for visibility.
            AppLog.Error("Remote game-exclusions refresh failed (non-fatal)", error);
            return null;
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

    private sealed record CacheEntry(DateTimeOffset FetchedAt, string[] Executables);
}
