using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.App.Services;

/// <summary>Resolves only reviewed square artwork shipped in ClypDat's source tree.</summary>
public sealed class OfficialGameArtService
{
    private const string AssetBaseUrl = "https://raw.githubusercontent.com/ClypLabs/ClypDat/master/native/official-game-art/";
    private static readonly Lazy<OfficialGameArtService> Packaged = new(LoadPackaged);
    private readonly IReadOnlyList<OfficialGameArtEntry> _entries;

    public OfficialGameArtService(IEnumerable<OfficialGameArtEntry> entries) =>
        _entries = entries.Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Asset) &&
                Uri.TryCreate(entry.OfficialSourceUrl, UriKind.Absolute, out var source) &&
                source.Scheme == Uri.UriSchemeHttps)
            .ToArray();

    /// <summary>Returns reviewed ClypDat-hosted square art, or null for a miss.</summary>
    public static async Task<string?> ResolveAsync(string detectionKey, string displayName)
    {
        var curated = Packaged.Value.Resolve(detectionKey, displayName);
        return curated ?? await DiscordDetectableGameCatalog.ResolveImageUrlAsync(detectionKey, displayName).ConfigureAwait(false);
    }

    public static async Task<string?> ResolveGameProfileUrlAsync(string detectionKey, string displayName)
    {
        var curated = Packaged.Value.ResolveGameProfileUrl(detectionKey, displayName);
        return curated ?? await DiscordDetectableGameCatalog.ResolveProfileUrlAsync(detectionKey, displayName).ConfigureAwait(false);
    }

    public string? Resolve(string detectionKey, string displayName)
    {
        var entry = Find(detectionKey, displayName);

        return entry is null ? null : AssetBaseUrl + Uri.EscapeDataString(entry.Asset);
    }

    public string? ResolveGameProfileUrl(string detectionKey, string displayName)
    {
        var entry = Find(detectionKey, displayName);
        if (entry is null || !Uri.TryCreate(entry.OfficialSourceUrl, UriKind.Absolute, out var source)) return null;
        var parts = source.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["app-icons", var gameId, ..] && ulong.TryParse(gameId, out _)
            ? $"https://discord.com/games/{gameId}"
            : null;
    }

    private OfficialGameArtEntry? Find(string detectionKey, string displayName) =>
        _entries.FirstOrDefault(candidate =>
            candidate.DetectionKeys.Any(key => string.Equals(key, detectionKey, StringComparison.OrdinalIgnoreCase)))
        ?? _entries.FirstOrDefault(candidate =>
            candidate.DisplayNameAliases.Any(alias => string.Equals(alias, displayName, StringComparison.OrdinalIgnoreCase)));

    internal IReadOnlyList<OfficialGameArtEntry> Entries => _entries;

    private static OfficialGameArtService LoadPackaged()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "official-game-art.json");
            var document = JsonSerializer.Deserialize<OfficialGameArtManifest>(File.ReadAllText(path), JsonOptions);
            return new OfficialGameArtService(document?.Games ?? []);
        }
        catch (Exception error)
        {
            AppLog.Error("Official game-art manifest load failed (non-fatal)", error);
            return new OfficialGameArtService([]);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

public sealed record OfficialGameArtEntry(string Asset, string OfficialSourceUrl, string[] DetectionKeys, string[] DisplayNameAliases);
public sealed record OfficialGameArtManifest(int SchemaVersion, OfficialGameArtEntry[] Games);

// Discord publishes this public catalogue for its own game detection. It is
// the only fallback: no store search, cropped portraits, or executable icons.
// A daily disk cache avoids downloading the roughly 24,000-entry catalogue on
// every launch while making newly published Discord game profiles appear soon.
internal static class DiscordDetectableGameCatalog
{
    private const string CatalogueUrl = "https://discord.com/api/v10/applications/detectable";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CatalogueIndex? _index;
    private static string CachePath => Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "discord-detectable-games.json");

    public static async Task<string?> ResolveImageUrlAsync(string detectionKey, string displayName)
    {
        var game = await FindAsync(detectionKey, displayName).ConfigureAwait(false);
        return game is not null && !string.IsNullOrEmpty(game.IconHash)
            ? $"https://cdn.discordapp.com/app-icons/{game.Id}/{game.IconHash}.png?size=1024"
            : null;
    }

    public static async Task<string?> ResolveProfileUrlAsync(string detectionKey, string displayName)
    {
        var game = await FindAsync(detectionKey, displayName).ConfigureAwait(false);
        return game is null ? null : $"https://discord.com/games/{game.Id}";
    }

    private static async Task<CatalogueGame?> FindAsync(string detectionKey, string displayName)
    {
        var index = await LoadAsync().ConfigureAwait(false);
        var steamAppId = detectionKey.StartsWith("steam-", StringComparison.OrdinalIgnoreCase)
            ? detectionKey["steam-".Length..]
            : null;
        var normalizedName = Normalize(displayName);
        var normalizedKey = Normalize(detectionKey[(detectionKey.IndexOf('-') + 1)..]);
        // Earliest catalogue position wins across all three lookups, which is
        // what the old first-match scan over the whole list returned.
        var best = int.MaxValue;
        if (steamAppId is not null && index.BySteamAppId.TryGetValue(steamAppId, out var bySteam)) best = Math.Min(best, bySteam);
        if (normalizedName.Length > 0 && index.ByName.TryGetValue(normalizedName, out var byName)) best = Math.Min(best, byName);
        if (normalizedKey.Length > 0 && index.ByName.TryGetValue(normalizedKey, out var byKey)) best = Math.Min(best, byKey);
        return best == int.MaxValue ? null : index.Games[best];
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // The catalogue is ~13MB of JSON and ~24,000 records, and this used to keep
    // all of it (plus the whole file as one string while parsing) for the life
    // of the process to answer an occasional art lookup. It now streams the
    // file once and keeps only the lookup keys and the two fields a match uses.
    private static async Task<CatalogueIndex> LoadAsync()
    {
        if (_index is not null) return _index;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_index is not null) return _index;
            var cache = new FileInfo(CachePath);
            if (!cache.Exists || DateTime.UtcNow - cache.LastWriteTimeUtc >= RefreshInterval)
            {
                try
                {
                    await DownloadAsync().ConfigureAwait(false);
                }
                // A day-old catalogue still answers nearly every lookup.
                catch (Exception error) when (cache.Exists)
                {
                    AppLog.Info($"Discord detectable-game catalogue refresh failed; using the cached copy ({error.Message}).");
                }
            }
            await using var stream = File.OpenRead(CachePath);
            return _index = await BuildIndexAsync(stream).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            AppLog.Error("Discord detectable-game catalogue lookup failed (non-fatal)", error);
            return _index = CatalogueIndex.Empty;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task<CatalogueIndex> BuildIndexAsync(Stream json)
    {
        var games = new List<CatalogueGame>();
        var bySteamAppId = new Dictionary<string, int>(StringComparer.Ordinal);
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var game in JsonSerializer.DeserializeAsyncEnumerable<DiscordDetectableGame>(json, JsonOptions).ConfigureAwait(false))
        {
            if (game is null || string.IsNullOrEmpty(game.Id)) continue;
            var position = games.Count;
            games.Add(new CatalogueGame(game.Id, game.IconHash));
            foreach (var sku in game.ThirdPartySkus ?? [])
            {
                if (string.Equals(sku.Distributor, "steam", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sku.Id))
                    bySteamAppId.TryAdd(sku.Id, position);
            }
            AddName(byName, game.Name, position);
            foreach (var alias in game.Aliases ?? []) AddName(byName, alias, position);
            foreach (var executable in game.Executables ?? []) AddName(byName, Path.GetFileNameWithoutExtension(executable.Name), position);
        }

        games.TrimExcess();
        bySteamAppId.TrimExcess();
        byName.TrimExcess();
        return new CatalogueIndex(games, bySteamAppId, byName);
    }

    private static void AddName(Dictionary<string, int> byName, string? value, int position)
    {
        if (string.IsNullOrEmpty(value)) return;
        var normalized = Normalize(value);
        if (normalized.Length > 0) byName.TryAdd(normalized, position);
    }

    private static async Task DownloadAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        var partial = CachePath + ".partial";
        // HttpClient.Timeout stops at the response headers for a stream, so
        // the body gets its own deadline.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using (var source = await Http.GetStreamAsync(CatalogueUrl, deadline.Token).ConfigureAwait(false))
        await using (var target = File.Create(partial))
        {
            await source.CopyToAsync(target, deadline.Token).ConfigureAwait(false);
        }
        File.Move(partial, CachePath, overwrite: true);
    }

    internal sealed record CatalogueGame(string Id, string? IconHash);

    internal sealed record CatalogueIndex(
        IReadOnlyList<CatalogueGame> Games,
        IReadOnlyDictionary<string, int> BySteamAppId,
        IReadOnlyDictionary<string, int> ByName)
    {
        public static readonly CatalogueIndex Empty = new([], new Dictionary<string, int>(), new Dictionary<string, int>());
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record DiscordDetectableGame(
        string Id,
        string? Name,
        [property: JsonPropertyName("icon_hash")] string? IconHash,
        string[]? Aliases,
        [property: JsonPropertyName("third_party_skus")] DiscordSku[]? ThirdPartySkus,
        DiscordExecutable[]? Executables);

    private sealed record DiscordSku(string Distributor, string Id);
    private sealed record DiscordExecutable(string? Name);
}
