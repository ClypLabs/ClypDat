using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Converters;

public sealed class OnboardingDotBrushConverter : IValueConverter
{
    public static readonly OnboardingDotBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value as string ?? string.Empty;
        var step = parameter as string ?? string.Empty;
        return string.Equals(current, step, StringComparison.Ordinal)
            ? AppThemeService.Brush("AccentBrush", "#5864E8")
            : AppThemeService.Brush("Surface_2C3B48", "#2C3B48");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
