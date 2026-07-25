using System.Globalization;
using Avalonia.Data.Converters;

namespace ClypDat.App.Converters;

// Turns a 0-1 usage fraction into a stroked-arc Geometry path for the
// Library sidebar's storage ring (32x32, centered at 16,16) - Avalonia has
// no circular ProgressBar, so this hand-draws the filled portion as a
// single SVG arc segment starting at 12 o'clock and sweeping clockwise.
// Only meaningful once a storage limit is set (MainWindow.axaml only shows
// this Path when HasLibraryStorageLimit is true); the plain background
// Ellipse underneath is always visible as the ring's track.
public sealed class FractionToArcPathConverter : IValueConverter
{
    public static readonly FractionToArcPathConverter Instance = new();

    // Must match the sidebar ring's actual box in MainWindow.axaml: a 30x30
    // Panel/Ellipse with StrokeThickness 2.5, so the stroke's centerline
    // radius is (30 - 2.5) / 2 and the center is 15,15. These were still the
    // 32x32 values after the ring was resized to 30, which drew the progress
    // arc on a slightly larger circle offset from the track behind it.
    private const double CenterX = 15;
    private const double CenterY = 15;
    private const double Radius = 13.75;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double fraction) return string.Empty;
        // A sweep of exactly 0 or 360 degrees makes the arc's start/end
        // points coincide, which most arc renderers (Avalonia included)
        // collapse to nothing instead of drawing a ring - clamp just short
        // of the closed ends so a near-empty/near-full ring still renders.
        fraction = Math.Clamp(fraction, 0.002, 0.998);

        var startAngle = -Math.PI / 2;
        var endAngle = startAngle + fraction * 2 * Math.PI;

        var startX = CenterX + Radius * Math.Cos(startAngle);
        var startY = CenterY + Radius * Math.Sin(startAngle);
        var endX = CenterX + Radius * Math.Cos(endAngle);
        var endY = CenterY + Radius * Math.Sin(endAngle);
        var largeArcFlag = fraction > 0.5 ? 1 : 0;

        return string.Create(CultureInfo.InvariantCulture,
            $"M {startX:0.##},{startY:0.##} A {Radius:0.##},{Radius:0.##} 0 {largeArcFlag} 1 {endX:0.##},{endY:0.##}");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
