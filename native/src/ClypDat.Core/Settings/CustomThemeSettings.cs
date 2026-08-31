using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.Core.Settings;

public sealed class CustomThemeSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom theme";
    public string BaseColor { get; set; } = "#0D1116";
    public string AccentColor { get; set; } = "#5864E8";
}

public sealed record ThemeFile(int SchemaVersion, string Name, string BaseColor, string AccentColor);

public static class CustomThemeLibrary
{
    public const int ThemeFileSchemaVersion = 1;
    public const int RecentColorLimit = 8;

    public static bool IsCustomSelection(string? selection) =>
        selection?.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) == true;

    public static string Selection(CustomThemeSettings theme) => "custom:" + theme.Id;

    public static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        value.Skip(1).All(Uri.IsHexDigit);

    public static bool TryNormalizeName(string? value, IEnumerable<CustomThemeSettings> existing,
        string? exceptId, out string name, out string? error)
    {
        name = value?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 64) { error = "Theme name must be 1–64 characters."; return false; }
        var normalizedName = name;
        if (existing.Any(theme => !string.Equals(theme.Id, exceptId, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(theme.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)))
        { error = "Theme name already exists."; return false; }
        error = null;
        return true;
    }

    public static string UniqueName(string name, IEnumerable<CustomThemeSettings> existing)
    {
        name = name.Trim();
        if (!existing.Any(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!existing.Any(theme => string.Equals(theme.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    public static void AddRecent(AppSettings settings, params string[] colors)
    {
        settings.RecentThemeColors ??= new();
        foreach (var color in colors.Where(IsColor).Select(color => color.ToUpperInvariant()))
        {
            settings.RecentThemeColors.RemoveAll(old => string.Equals(old, color, StringComparison.OrdinalIgnoreCase));
            settings.RecentThemeColors.Insert(0, color);
        }
        if (settings.RecentThemeColors.Count > RecentColorLimit)
            settings.RecentThemeColors.RemoveRange(RecentColorLimit, settings.RecentThemeColors.Count - RecentColorLimit);
    }

    public static string Export(CustomThemeSettings theme) => JsonSerializer.Serialize(
        new ThemeFile(ThemeFileSchemaVersion, theme.Name, theme.BaseColor, theme.AccentColor),
        new JsonSerializerOptions { WriteIndented = true });

    public static bool TryImport(string json, IEnumerable<CustomThemeSettings> existing,
        out CustomThemeSettings? theme, out string? error)
    {
        theme = null;
        try
        {
            var file = JsonSerializer.Deserialize<ThemeFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (file is null || file.SchemaVersion != ThemeFileSchemaVersion) { error = "Unsupported theme schema."; return false; }
            if (!TryNormalizeName(file.Name, existing, null, out var name, out error) && error != "Theme name already exists.") return false;
            if (!IsColor(file.BaseColor) || !IsColor(file.AccentColor)) { error = "Theme colours must use #RRGGBB."; return false; }
            theme = new CustomThemeSettings { Name = UniqueName(name, existing), BaseColor = file.BaseColor.ToUpperInvariant(), AccentColor = file.AccentColor.ToUpperInvariant() };
            error = null;
            return true;
        }
        catch (JsonException) { error = "Theme file is not valid JSON."; return false; }
    }
}
