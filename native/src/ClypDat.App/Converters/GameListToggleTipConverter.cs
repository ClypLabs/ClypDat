using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// The sidebar's overflow-games toggle is a folder row: the tooltip has to say
// which way the next click goes, since the chevron alone is easy to read as
// decoration on a rail of icons.
public sealed class GameListToggleTipConverter : IValueConverter
{
    public static readonly GameListToggleTipConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Hide extra games" : "Show more games";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
