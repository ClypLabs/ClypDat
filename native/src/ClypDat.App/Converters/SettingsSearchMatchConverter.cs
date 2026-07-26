using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// Bound value is MainWindowViewModel.SettingsSearchText, ConverterParameter is
// the nav button's own section name - empty search matches everything, so the
// Settings nav looks unchanged until the user actually types something.
public sealed class SettingsSearchMatchConverter : IValueConverter
{
    public static readonly SettingsSearchMatchConverter Instance = new();

    // The individual setting names living on each section's own page -
    // matching only the section's own title (the original version of this)
    // meant searching "frame rate" found nothing, since "Replay Buffer" the
    // nav label never contains the word "frame". Kept here (not scraped from
    // the XAML at runtime) since it's just for narrowing the nav down to the
    // right page - MainWindowViewModel.SettingsSearchText's setter also
    // calls MatchesSection to auto-select the page once a query narrows to
    // exactly one match, so the user lands there without an extra click.
    private static readonly Dictionary<string, string[]> SectionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["General"] = new[]
        {
            "Launch ClypDat on Windows boot", "Start minimized on boot", "Show status panel",
            "Playback Paused indicator", "Scale clips with window size", "Hoverbar",
            "Combine sidebar filters", "Clip Filenames", "Template", "Rename all"
        },
        ["Game Detection"] = new[]
        {
            "Add a running game", "browse for an executable", "Game icons", "Refresh game icons",
            "Excluded from detection", "capture backend override"
        },
        ["Import from Medal"] = new[]
        {
            "Strip emoji from titles", "Copy instead of move", "Scan for Medal clips"
        },
        ["Replay Buffer"] = new[]
        {
            "Save hotkey", "Replay length", "Resolution", "Frame rate", "Capture backend",
            "Full session recording", "Destination folder", "Session codec",
            "Finalize in background", "Storage limit", "Custom limit"
        },
        ["Overlays and Notifications"] = new[]
        {
            "clip saved overlay", "Overlay position", "clip saved sound", "Sound volume"
        },
        ["Auto-Clip"] = new[] { "Auto Capture Events" },
        ["Audio"] = new[]
        {
            "Chat Audio App", "Multiple apps", "Microphone", "Multiple mics",
            "noise suppression", "Strength", "Audio sync offset"
        },
        ["Game Audio Exclusions"] = new[] { "Excluded", "exclusions" },
        ["About"] = new[]
        {
            "Check for updates", "View on GitHub", "Log folder", "Setup walkthrough", "License"
        },
    };

    public static bool MatchesSection(string query, string sectionName)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        if (sectionName.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return SectionKeywords.TryGetValue(sectionName, out var keywords) &&
               keywords.Any(keyword => keyword.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var query = (value as string) ?? string.Empty;
        var sectionName = parameter as string ?? string.Empty;
        return MatchesSection(query, sectionName);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
