using System.Globalization;
using Avalonia.Data.Converters;

namespace Eve.App.Converters;

// Generic double - ConverterParameter, clamped at 0 - used to derive a
// MaxWidth from a container's own bound width (e.g. a clip card's title
// needs to leave room for the rename pencil next to it, scaling with
// CardWidth instead of a flat guess that's either too tight on wide cards
// or overflows on the narrowest ones).
public sealed class SubtractDoubleConverter : IValueConverter
{
    public static readonly SubtractDoubleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width) return value;
        var reserve = parameter switch
        {
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            double number => number,
            _ => 0
        };

        return Math.Max(0, width - reserve);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
