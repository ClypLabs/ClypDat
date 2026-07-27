using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// Drives each Settings section's own content panel. Bound values are
// [SettingsSearchText, SelectedSettingsSection], ConverterParameter is the
// section's own name. With no active search, behaves exactly like the old
// single Binding did - only the selected section shows. Once the user
// types something, every section with at least one match shows at once
// (each still filtered down to its own matching rows via
// SettingsSearchHighlightConverter) instead of confining results to
// whichever section happened to be selected before the search started.
public sealed class SettingsSectionVisibleConverter : IMultiValueConverter
{
    public static readonly SettingsSectionVisibleConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var query = (values.Count > 0 ? values[0] as string : null) ?? string.Empty;
        var sectionName = parameter as string ?? string.Empty;

        if (string.IsNullOrWhiteSpace(query))
        {
            var selected = values.Count > 1 ? values[1] as string : null;
            return sectionName == selected;
        }

        return SettingsSearchMatchConverter.MatchesSection(query, sectionName);
    }
}
