using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ClypDat.App.Converters;

public sealed class AudioVolumeBrushConverter : IValueConverter
{
    public static readonly AudioVolumeBrushConverter Instance = new();
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#1598F5"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F4B73E"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#F05A63"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double volume && volume > 125 ? Red : value is double level && level > 100 ? Amber : Blue;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
