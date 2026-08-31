using System.Text.Json;

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
    public static Task<string?> ResolveAsync(string detectionKey, string displayName) =>
        Task.FromResult(Packaged.Value.Resolve(detectionKey, displayName));

    public string? Resolve(string detectionKey, string displayName)
    {
        var entry = _entries.FirstOrDefault(candidate =>
            candidate.DetectionKeys.Any(key => string.Equals(key, detectionKey, StringComparison.OrdinalIgnoreCase)))
            ?? _entries.FirstOrDefault(candidate =>
                candidate.DisplayNameAliases.Any(alias => string.Equals(alias, displayName, StringComparison.OrdinalIgnoreCase)));

        return entry is null ? null : AssetBaseUrl + Uri.EscapeDataString(entry.Asset);
    }

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
