using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// A scroll offset turned into the opacity of the fade drawn over that edge.
// The game rail's top fade used to be painted unconditionally, so at rest -
// nothing scrolled off the top, nothing to imply - it just dimmed the top few
// pixels of the first game icon. Fading it in over the first ConverterParameter
// pixels of travel means it only appears once there is something underneath it.
public sealed class ScrollFadeOpacityConverter : IValueConverter
{
    public static readonly ScrollFadeOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double offset) return 0d;
        var travel = parameter switch
        {
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            double number => number,
            _ => 14d
        };

        if (travel <= 0) return offset > 0 ? 1d : 0d;
        return Math.Clamp(offset / travel, 0d, 1d);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
