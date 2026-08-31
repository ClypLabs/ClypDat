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

    public (double Hue, double Saturation, double Value) ToHsv()
    {
        var red = Red / 255d;
        var green = Green / 255d;
        var blue = Blue / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;
        var hue = delta == 0 ? 0 : max == red ? 60 * ((green - blue) / delta % 6) :
            max == green ? 60 * ((blue - red) / delta + 2) : 60 * ((red - green) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    public static ThemeColor FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var match = value - chroma;
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return new ThemeColor((byte)Math.Round((red + match) * 255), (byte)Math.Round((green + match) * 255), (byte)Math.Round((blue + match) * 255));
    }
}
