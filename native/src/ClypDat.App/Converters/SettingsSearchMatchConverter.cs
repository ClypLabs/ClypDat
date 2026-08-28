using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using ClypDat.App.Services;

namespace ClypDat.App.Converters;

// Bound value is MainWindowViewModel.SettingsSearchText, ConverterParameter is
// the nav button's own section name - empty search matches everything, so the
// Settings nav looks unchanged until the user actually types something.
public sealed class SettingsSearchMatchConverter : IValueConverter
{
    public static readonly SettingsSearchMatchConverter Instance = new();

    // Declared before SectionKeywords - static field initializers run in
    // declaration order, and SectionKeywords' own "Auto-Clip" entry reads
    // this one.
    private static readonly string[] AutoClipGameNames = AutoClipCatalog.Active.Select(game => game.Name).ToArray();

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
        // Card headers (Startup, Layout, Recording, Encoding, ...) are listed
        // alongside the individual setting names: the headers are what a user
        // scanning the page actually remembers, so they have to be searchable
        // too, and a card whose header matches still needs its section to
        // survive the nav filter or the page it lives on never appears.
        ["General"] = new[]
        {
            "Startup", "Launch ClypDat on Windows boot", "Start minimized on boot", "Process priority", "Automatically Focus ClypDat", "Game exit",
            "Layout", "Show status panel",
            "Playback Paused indicator", "Scale clips with window size",
            "Combine sidebar filters", "Clip Filenames", "Template", "Rename all"
        },
        ["Game Detection"] = new[]
        {
            "Add games", "Add a running game", "browse for an executable", "Game icons", "Refresh game icons",
            "Excluded games", "Excluded from detection", "capture backend override"
        },
        ["Import Clips"] = new[]
        {
            "Import options", "Medal", "SteelSeries", "Moments", "Strip emoji from titles", "Copy instead of move",
            "Scan", "Scan for Medal clips", "Scan for SteelSeries clips"
        },
        ["Replay Buffer"] = new[]
        {
            "Hotkey", "Save hotkey", "Recording", "Replay length", "Resolution", "Frame rate",
            "Encoding", "Encoder Preset", "Rate control", "Quality", "Bitrate",
            "Full session recording", "Destination folder", "Session codec",
            "Finalize in background", "Storage limit", "Custom limit"
        },
        ["Overlays and Notifications"] = new[]
        {
            "Overlays", "clip saved overlay", "Hide overlays from screen capture",
            "Show new clips when a game closes",
            "clipping started overlay", "auto-clip detected overlay", "auto-clip failed overlay",
            "Overlay position", "Sound", "clip saved sound", "Sound volume"
        },
        // Plus every individual game's own name (Counter-Strike 2, Dota 2,
        // etc.) via AutoClipGameNames below - a per-game name never appears
        // in the section's own static keyword list, since the catalog is
        // the actual source of truth for what games exist.
        ["Auto-Clip"] = new[] { "Auto Capture", "Auto Capture Events", "Competitive", "Deathmatch Clipping", "Deathmatch" }.Concat(AutoClipGameNames).ToArray(),
        ["Audio"] = new[]
        {
            "Audio sources", "Chat Audio App", "Multiple apps", "Microphone", "Multiple mics",
            "Channels", "Mono", "Stereo"
        },
        ["Game Audio Exclusions"] = new[] { "Excluded apps", "Excluded", "exclusions" },
        ["Custom Game Settings"] = new[]
        {
            "Per-game settings", "Per game", "Custom game", "Game overrides", "Override",
            "Add Game", "Add Setting", "Recording Quality", "Replay Length", "Full Session"
        },
        ["Themes"] = new[]
        {
            "Theme", "Preset themes", "System", "ClypDat Blue", "Violet", "Emerald", "Rose", "Amber",
            "Custom themes", "Custom colours"
        },
        ["Discord Rich Presence"] = new[]
        {
            "Discord", "Rich Presence", "Discord Rich Presence", "Get ClypDat", "Profile link"
        },
        ["About"] = new[]
        {
            "Updates", "Check for updates", "Source and Licenses", "View on GitHub", "Diagnostics",
            "Log folder", "Get Started", "Setup walkthrough", "License",
        },
    };

    // sectionName can be several, '|'-separated - used by the nav's own
    // small-caps category headers (GENERAL/CAPTURE/ABOUT), which aren't
    // themselves a section but should only show while at least one section
    // underneath them is still showing. Searching "Sidebar filters" used to
    // still show GENERAL, CAPTURE, and ABOUT as floating orphaned labels
    // with every actual button beneath each of them hidden, since those
    // headers had no visibility condition of their own at all.
    public static bool MatchesSection(string query, string sectionName)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        return sectionName.Split('|').Any(name => MatchesSingleSection(query, name));
    }

    private static bool MatchesSingleSection(string query, string sectionName)
    {
        var haystack = sectionName;
        if (SectionKeywords.TryGetValue(sectionName, out var keywords)) haystack += " " + string.Join(' ', keywords);
        return SettingsSearchMatching.MatchesRelative(haystack, query);
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
