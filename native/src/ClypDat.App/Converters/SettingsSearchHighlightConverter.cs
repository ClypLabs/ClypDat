using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// Bound value is MainWindowViewModel.SettingsSearchText, ConverterParameter is
// a specific setting's own label text. Empty query matches everything (so
// Settings looks completely normal until the user actually searches); a
// non-empty query matches only settings whose label actually contains it -
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
        var query = (value as string)?.Trim() ?? string.Empty;
        if (query.Length == 0) return true;

        var label = parameter as string ?? string.Empty;
        return label.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
