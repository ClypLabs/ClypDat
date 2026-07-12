using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Eve.App.Converters;

public sealed class BoolToMuteIconConverter : IValueConverter
{
    public static readonly BoolToMuteIconConverter Instance = new();

    private const string UnmutedGeometry = "M 3,8 L 6,8 L 11,4 L 11,16 L 6,12 L 3,12 Z";
    private const string MutedGeometry = "M 3,8 L 6,8 L 11,4 L 11,16 L 6,12 L 3,12 Z M 13,6 L 18,14 M 18,6 L 13,14";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? MutedGeometry : UnmutedGeometry;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToMuteBrushConverter : IValueConverter
{
    public static readonly BoolToMuteBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Brush.Parse("#D85E61") : Brush.Parse("#D1DEE9");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.4 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
