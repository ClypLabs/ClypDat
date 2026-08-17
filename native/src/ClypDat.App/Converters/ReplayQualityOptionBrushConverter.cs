using System.Globalization;
using Avalonia.Media;
using Avalonia.Data.Converters;
using ClypDat.App.Services;

namespace ClypDat.App.Converters;

public sealed class ReplayQualityOptionBrushConverter : IValueConverter
{
    public static readonly ReplayQualityOptionBrushConverter Instance = new();

    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#B8C7D8"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#FF9AA2"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var exceedsDefault = parameter?.ToString() switch
        {
            "Resolution" => value is ResolutionOption option && option.Height > 1080,
            "FrameRate" => value is int frameRate && frameRate > 60,
            "Bitrate" => value is string bitrate && int.TryParse(bitrate.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var mbps) && mbps > 15,
            _ => false
        };

        return exceedsDefault ? WarningBrush : NormalBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
