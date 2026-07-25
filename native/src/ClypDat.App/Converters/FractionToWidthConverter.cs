using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// fraction (0-1) x ConverterParameter (the track's total pixel width) = a
// segment's pixel Width - used to size the ClypDat-clips/rest-of-PC
// segments of the storage flyout's usage bar, since Avalonia has no way to
// bind a per-column Grid width to a bound fraction directly.
public sealed class FractionToWidthConverter : IValueConverter
{
    public static readonly FractionToWidthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double fraction) return 0.0;
        var totalWidth = parameter switch
        {
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            double number => number,
            _ => 0.0
        };

        return Math.Max(0, fraction * totalWidth);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
