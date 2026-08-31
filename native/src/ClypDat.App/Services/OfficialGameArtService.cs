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
    private static IReadOnlyList<DiscordDetectableGame>? _games;
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

    private static async Task<DiscordDetectableGame?> FindAsync(string detectionKey, string displayName)
    {
        var games = await LoadAsync().ConfigureAwait(false);
        var steamAppId = detectionKey.StartsWith("steam-", StringComparison.OrdinalIgnoreCase)
            ? detectionKey["steam-".Length..]
            : null;
        var normalizedName = Normalize(displayName);
        var normalizedKey = Normalize(detectionKey[(detectionKey.IndexOf('-') + 1)..]);
        return games.FirstOrDefault(game =>
            steamAppId is not null && (game.ThirdPartySkus ?? []).Any(sku =>
                string.Equals(sku.Distributor, "steam", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sku.Id, steamAppId, StringComparison.Ordinal))
            || Matches(game, normalizedName)
            || Matches(game, normalizedKey));
    }

    private static bool Matches(DiscordDetectableGame game, string value) => !string.IsNullOrEmpty(value) &&
        (string.Equals(Normalize(game.Name), value, StringComparison.Ordinal) ||
         (game.Aliases ?? []).Any(alias => string.Equals(Normalize(alias), value, StringComparison.Ordinal)) ||
         (game.Executables ?? []).Any(executable => string.Equals(Normalize(Path.GetFileNameWithoutExtension(executable.Name)), value, StringComparison.Ordinal)));

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static async Task<IReadOnlyList<DiscordDetectableGame>> LoadAsync()
    {
        if (_games is not null) return _games;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_games is not null) return _games;
            var cache = new FileInfo(CachePath);
            var json = cache.Exists && DateTime.UtcNow - cache.LastWriteTimeUtc < RefreshInterval
                ? await File.ReadAllTextAsync(CachePath).ConfigureAwait(false)
                : await DownloadAsync().ConfigureAwait(false);
            return _games = JsonSerializer.Deserialize<DiscordDetectableGame[]>(json, JsonOptions) ?? [];
        }
        catch (Exception error)
        {
            AppLog.Error("Discord detectable-game catalogue lookup failed (non-fatal)", error);
            return _games = [];
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> DownloadAsync()
    {
        var json = await Http.GetStringAsync(CatalogueUrl).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, json).ConfigureAwait(false);
        return json;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record DiscordDetectableGame(
        string Id,
        string Name,
        [property: JsonPropertyName("icon_hash")] string? IconHash,
        string[]? Aliases,
        [property: JsonPropertyName("third_party_skus")] DiscordSku[]? ThirdPartySkus,
        DiscordExecutable[]? Executables);

    private sealed record DiscordSku(string Distributor, string Id);
    private sealed record DiscordExecutable(string Name);
}
