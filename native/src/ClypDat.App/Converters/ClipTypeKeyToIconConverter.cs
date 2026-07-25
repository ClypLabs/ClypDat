using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// Maps a ClipTypeFilterOptions row's Key (the fixed ClipTypeManual/AutoClip/
// Vod/MedalImport constants in MainWindowViewModel) to the PathIcon glyph
// the Library sidebar's icon-only Sections rail shows for it - keeps the
// icon rail data-driven off the same ItemsControl the text labels/counts
// already come from, instead of four hand-written buttons that'd drift out
// of sync with RecomputeGameFilterBadges (order, which keys exist at all).
public sealed class ClipTypeKeyToIconConverter : IValueConverter
{
    public static readonly ClipTypeKeyToIconConverter Instance = new();

    private const string PencilIcon = "M20.71,7.04C21.1,6.65 21.1,6 20.71,5.63L18.37,3.29C18,2.9 17.35,2.9 16.96,3.29L15.12,5.12L18.87,8.87M3,17.25V21H6.75L17.81,9.93L14.06,6.18L3,17.25Z";
    private const string BoltIcon = "M7,2v11h3v9l7-12h-4l4-8H7z";
    private const string MovieIcon = "M18,4l2,4h-3l-2-4h-2l2,4h-3l-2-4H8l2,4H7L5,4H4C2.9,4,2.01,4.9,2.01,6L2,18c0,1.1,0.9,2,2,2h16c1.1,0,2-0.9,2-2V4H18z";
    private const string ImportIcon = "M19,9h-4V3H9v6H5l7,7L19,9z M5,18v2h14v-2H5z";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "Manual" => PencilIcon,
            "AutoClip" => BoltIcon,
            "Vod" => MovieIcon,
            "MedalImport" => ImportIcon,
            _ => MovieIcon
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
