using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Eve.App.Converters;

public sealed class OnboardingDotBrushConverter : IValueConverter
{
    public static readonly OnboardingDotBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value as string ?? string.Empty;
        var step = parameter as string ?? string.Empty;
        return string.Equals(current, step, StringComparison.Ordinal)
            ? Brush.Parse("#5864E8")
            : Brush.Parse("#2C3B48");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
