using System.Globalization;

namespace ClypDat.Core.Settings;

public readonly record struct ThemeColor(byte Red, byte Green, byte Blue)
{
    public string Hex => $"#{Red:X2}{Green:X2}{Blue:X2}";

    public static bool TryParseHex(string? value, out ThemeColor color)
    {
        color = default;
        if (!CustomThemeLibrary.IsColor(value)) return false;
        if (!byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue)) return false;
        color = new ThemeColor(red, green, blue);
        return true;
    }

    public static bool TryFromRgb(int red, int green, int blue, out ThemeColor color)
    {
        color = default;
        if (red is < 0 or > 255 || green is < 0 or > 255 || blue is < 0 or > 255) return false;
        color = new ThemeColor((byte)red, (byte)green, (byte)blue);
        return true;
    }
}
