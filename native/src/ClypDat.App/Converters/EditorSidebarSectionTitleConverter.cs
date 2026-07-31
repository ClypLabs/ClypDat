using System.Globalization;
using Avalonia.Data.Converters;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Converters;

public sealed class EditorSidebarSectionTitleConverter : IValueConverter
{
    public static readonly EditorSidebarSectionTitleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        EditorSidebarSection.Info => "INFO",
        EditorSidebarSection.Effects => "EFFECTS",
        EditorSidebarSection.Export => "EXPORT",
        _ => string.Empty
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
