using System.Text.Json;

namespace ClypDat.App.Services;

public static class RemoteGameCatalogService
{
    private const string CatalogUrl = "https://raw.githubusercontent.com/ClypLabs/ClypDat/master/native/game-catalog.json";
    private const string CacheFileName = "remote-game-catalog.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    private static string CachePath => Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, CacheFileName);

    public static IReadOnlyList<GameCatalogEntry> LoadCached()
    {
        try { return File.Exists(CachePath) ? GameCatalogRules.Parse(File.ReadAllText(CachePath)) : Array.Empty<GameCatalogEntry>(); }
        catch { return Array.Empty<GameCatalogEntry>(); }
    }

    public static async Task<IReadOnlyList<GameCatalogEntry>?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(CachePath) && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(CachePath) < RefreshInterval) return null;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-GameCatalog");
            var json = await client.GetStringAsync(CatalogUrl, cancellationToken);
            var rules = GameCatalogRules.Parse(json);
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, json);
            return rules;
        }
        catch (Exception error)
        {
            AppLog.Error("Remote game-catalog refresh failed (non-fatal)", error);
            return null;
        }
    }
}
