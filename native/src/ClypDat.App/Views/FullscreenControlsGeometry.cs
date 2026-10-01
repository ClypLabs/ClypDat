using Avalonia;

namespace ClypDat.App.Views;

internal static class FullscreenControlsGeometry
{
    internal const double MaximumWidth = 900;
    internal const double Inset = 24;
    internal static bool StackVolume(double width) => width < 760;
    internal static bool StackTime(double width) => width < 480;

    internal static double Width(PixelRect viewport, double scale) =>
        Math.Clamp(viewport.Width / ValidScale(scale) - Inset * 2, 1, MaximumWidth);

    internal static PixelRect Place(PixelRect viewport, double scale, double height)
    {
        scale = ValidScale(scale);
        var margin = (int)Math.Round(Inset * scale);
        var width = Math.Min(viewport.Width, HoverBarGeometry.PixelSize(Width(viewport, scale), scale));
        var availableHeight = Math.Max(1, viewport.Height - margin * 2);
        var pixelsHigh = Math.Min(availableHeight, HoverBarGeometry.PixelSize(height, scale));
        var bottomInset = Math.Min(margin, Math.Max(0, viewport.Height - pixelsHigh));
        return new PixelRect(viewport.X + (viewport.Width - width) / 2,
            viewport.Bottom - bottomInset - pixelsHigh, width, pixelsHigh);
    }

    private static double ValidScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1;
}
