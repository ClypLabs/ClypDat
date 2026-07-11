using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace Eve.App.Converters;

public sealed class OverlayPositionToHorizontalAlignmentConverter : IValueConverter
{
    public static readonly OverlayPositionToHorizontalAlignmentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var position = value as string ?? string.Empty;
        return position.Contains("Left", StringComparison.OrdinalIgnoreCase) ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class OverlayPositionToVerticalAlignmentConverter : IValueConverter
{
    public static readonly OverlayPositionToVerticalAlignmentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var position = value as string ?? string.Empty;
        if (position.Contains("Top", StringComparison.OrdinalIgnoreCase)) return VerticalAlignment.Top;
        if (position.Contains("Bottom", StringComparison.OrdinalIgnoreCase)) return VerticalAlignment.Bottom;
        return VerticalAlignment.Center;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
