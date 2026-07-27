using System.Text.Json;

namespace ClypDat.App.Services;

public sealed class GameCatalogDocument
{
    public int SchemaVersion { get; set; }
    public List<GameCatalogEntry> Games { get; set; } = new();
}

public sealed class GameCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<int> SteamAppIds { get; set; } = new();
    public List<GameWindowMatcher> Matchers { get; set; } = new();
    public List<GameWindowMatcher> BlockedWindows { get; set; } = new();
}

// Empty matcher fields mean "do not constrain this field". Non-empty lists
// are OR'd within a field and AND'd with every other populated field.
public sealed class GameWindowMatcher
{
    public string Executable { get; set; } = string.Empty;
    public List<string> ClassEquals { get; set; } = new();
    public List<string> ClassContains { get; set; } = new();
    public List<string> TitleEquals { get; set; } = new();
    public List<string> TitleContains { get; set; } = new();
    public int? MinWidth { get; set; }
    public int? MinHeight { get; set; }
    public int? MaxWidth { get; set; }
    public int? MaxHeight { get; set; }
}

public static class GameCatalogRules
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<GameCatalogEntry> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<GameCatalogEntry>();

        using var document = JsonDocument.Parse(json);
        // Keep compatibility with ClypDat's previous local file: { "game.exe": "Game" }.
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            !document.RootElement.EnumerateObject().Any(property => string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase)))
        {
            return document.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                .Select(property => new GameCatalogEntry
                {
                    Id = property.Name,
                    DisplayName = property.Value.GetString()!,
                    Matchers = new List<GameWindowMatcher> { new() { Executable = property.Name } }
                })
                .ToArray();
        }

        var parsed = JsonSerializer.Deserialize<GameCatalogDocument>(json, Options)
                     ?? throw new InvalidDataException("Game catalog is empty.");
        if (parsed.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported game catalog schema {parsed.SchemaVersion}.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<GameCatalogEntry>();
        foreach (var entry in parsed.Games ?? new List<GameCatalogEntry>())
        {
            entry.Id = entry.Id?.Trim() ?? string.Empty;
            entry.DisplayName = entry.DisplayName?.Trim() ?? string.Empty;
            entry.Matchers ??= new List<GameWindowMatcher>();
            entry.BlockedWindows ??= new List<GameWindowMatcher>();
            entry.SteamAppIds ??= new List<int>();
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.DisplayName) || !ids.Add(entry.Id)) continue;
            entry.Matchers = entry.Matchers.Where(matcher => IsValidMatcher(matcher)).ToList();
            entry.BlockedWindows = entry.BlockedWindows.Where(matcher => IsValidMatcher(matcher, executableRequired: false)).ToList();
            if (entry.Matchers.Count == 0 && entry.SteamAppIds.Count == 0) continue;
            valid.Add(entry);
        }

        return valid;
    }

    public static bool Matches(GameWindowMatcher matcher, string executable, string title, string className, int width, int height)
    {
        if (!string.IsNullOrWhiteSpace(matcher.Executable) &&
            !string.Equals(matcher.Executable, executable, StringComparison.OrdinalIgnoreCase)) return false;
        if (!MatchesExact(matcher.ClassEquals, className) || !MatchesContains(matcher.ClassContains, className)) return false;
        if (!MatchesExact(matcher.TitleEquals, title) || !MatchesContains(matcher.TitleContains, title)) return false;
        if (matcher.MinWidth is int minWidth && width < minWidth) return false;
        if (matcher.MinHeight is int minHeight && height < minHeight) return false;
        if (matcher.MaxWidth is int maxWidth && width > maxWidth) return false;
        if (matcher.MaxHeight is int maxHeight && height > maxHeight) return false;
        return true;
    }

    private static bool IsValidMatcher(GameWindowMatcher matcher, bool executableRequired = true)
    {
        matcher.Executable = matcher.Executable?.Trim() ?? string.Empty;
        matcher.ClassEquals ??= new List<string>();
        matcher.ClassContains ??= new List<string>();
        matcher.TitleEquals ??= new List<string>();
        matcher.TitleContains ??= new List<string>();
        return !executableRequired || !string.IsNullOrWhiteSpace(matcher.Executable) ||
               matcher.ClassEquals.Count > 0 || matcher.ClassContains.Count > 0 || matcher.TitleEquals.Count > 0 || matcher.TitleContains.Count > 0;
    }

    private static bool MatchesExact(IEnumerable<string> values, string actual)
    {
        var valuesArray = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return valuesArray.Length == 0 || valuesArray.Any(value => string.Equals(value, actual, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesContains(IEnumerable<string> values, string actual)
    {
        var valuesArray = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return valuesArray.Length == 0 || valuesArray.Any(value => actual.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}
