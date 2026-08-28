using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Converters;

public sealed class AudioVolumeBrushConverter : IValueConverter
{
    public static readonly AudioVolumeBrushConverter Instance = new();
    private static readonly IBrush Blue = AppThemeService.Brush("Semantic_1598F5", "#1598F5");
    private static readonly IBrush Amber = AppThemeService.Brush("Semantic_F4B73E", "#F4B73E");
    private static readonly IBrush Red = AppThemeService.Brush("Semantic_F05A63", "#F05A63");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double volume && volume > 125 ? Red : value is double level && level > 100 ? Amber : Blue;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
