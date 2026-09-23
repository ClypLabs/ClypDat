using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace ClypDat.App.Converters;

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

// The stored position ("Center Left") is a settings value and stays as it is;
// this is only how it reads on screen: sentence case, Australian spelling.
public sealed class OverlayPositionLabelConverter : IValueConverter
{
    public static readonly OverlayPositionLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Label(value as string ?? string.Empty);

    internal static string Label(string position)
    {
        var words = position.Replace("Center", "Centre", StringComparison.OrdinalIgnoreCase)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return string.Empty;
        return string.Join(' ', words.Select((word, index) => index == 0 ? word : word.ToLowerInvariant()));
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
