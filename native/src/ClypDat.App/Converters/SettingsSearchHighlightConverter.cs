using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// Bound value is MainWindowViewModel.SettingsSearchText, ConverterParameter is
// the specific setting label's own text - true only once the query actually
// names that setting (not just its section), so Classes.searchHighlight can
// pick it out with the OS accent colour among everything else on the page.
public sealed class SettingsSearchHighlightConverter : IValueConverter
{
    public static readonly SettingsSearchHighlightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var query = (value as string)?.Trim() ?? string.Empty;
        if (query.Length == 0) return false;

        var label = parameter as string ?? string.Empty;
        return label.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
