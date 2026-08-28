using System.Globalization;
using Avalonia.Media;
using Avalonia.Data.Converters;
using ClypDat.App.Services;

namespace ClypDat.App.Converters;

public sealed class ReplayQualityOptionBrushConverter : IValueConverter
{
    public static readonly ReplayQualityOptionBrushConverter Instance = new();

    private static readonly IBrush NormalBrush = AppThemeService.Brush("Text_B8C7D8", "#B8C7D8");
    private static readonly IBrush WarningBrush = AppThemeService.Brush("Semantic_FF9AA2", "#FF9AA2");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var exceedsDefault = parameter?.ToString() switch
        {
            "Resolution" => value is ResolutionOption option && option.Height > 1080,
            "FrameRate" => value is int frameRate && frameRate > 60,
            "Bitrate" => value is string bitrate && int.TryParse(new string(bitrate.TakeWhile(char.IsDigit).ToArray()), out var mbps) && mbps > 20,
            _ => false
        };

        return exceedsDefault ? WarningBrush : NormalBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
