using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// Bound value is MainWindowViewModel.SettingsSearchText, ConverterParameter is
// a specific setting's own label text - or several, '|'-separated, when the
// element being bound is a settingsCard shared by more than one setting (the
// card as a whole should only disappear once NONE of its settings match, not
// the instant any single one stops). Empty query matches everything (so
// Settings looks completely normal until the user actually searches); a
// non-empty query matches if ANY of the '|'-separated labels contains it -
// bound to each setting's row-wrapping element's IsVisible, so searching
// narrows the page down to just what's relevant instead of leaving
// everything on screen and only highlighting a match (see SettingsHighlight
// for the in-label substring highlight, a separate concern from this row-
// level show/hide).
public sealed class SettingsSearchHighlightConverter : IValueConverter
{
    public static readonly SettingsSearchHighlightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var query = (value as string) ?? string.Empty;
        var haystack = (parameter as string ?? string.Empty).Replace('|', ' ');
        return SettingsSearchMatching.MatchesRelative(haystack, query);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
