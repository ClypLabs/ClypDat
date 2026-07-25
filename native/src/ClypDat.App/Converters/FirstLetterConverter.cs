using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// Games list in the Library sidebar's icon rail has no per-game artwork
// (only CS2/Dota2/League ship cover images, and those are for library
// cards, not a 32px nav badge) - a single uppercase initial in a circle is
// the same fallback-avatar convention Discord/Slack use for anything
// without a real icon, and it scales to any game name automatically.
public sealed class FirstLetterConverter : IValueConverter
{
    public static readonly FirstLetterConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        return string.IsNullOrEmpty(text) ? "?" : char.ToUpperInvariant(text[0]).ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
