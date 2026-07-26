using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// Bound value is MainWindowViewModel.SettingsSearchText, ConverterParameter is
// the nav button's own section name - empty search matches everything, so the
// Settings nav looks unchanged until the user actually types something.
public sealed class SettingsSearchMatchConverter : IValueConverter
{
    public static readonly SettingsSearchMatchConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var query = (value as string)?.Trim() ?? string.Empty;
        if (query.Length == 0) return true;

        var sectionName = parameter as string ?? string.Empty;
        return sectionName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
