using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Controls;

// Drawn only in the editor's owned overlay window, never into exported frames.
internal sealed class SpotifyOverlayAdorner : Control
{
    private static readonly Pen Selection = new(new SolidColorBrush(Color.FromRgb(99, 235, 171)), 1.5);
    private static readonly Pen HandleOutline = new(new SolidColorBrush(Color.FromRgb(18, 44, 34)), 1);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 3 || bounds.Height < 3) return;
        context.DrawRectangle(null, Selection, bounds.Deflate(1), 3, 3);
        var size = Math.Min(8, Math.Min(bounds.Width, bounds.Height) / 3);
        foreach (var x in new[] { 1.0, bounds.Width - size - 1 })
        foreach (var y in new[] { 1.0, bounds.Height - size - 1 })
            context.DrawRectangle(Brushes.White, HandleOutline, new Rect(x, y, size, size), 2, 2);
    }

    public static SpotifyOverlayDragMode HitTest(Point point, Size size)
    {
        var reach = Math.Min(16, Math.Min(size.Width, size.Height) * .35);
        if (point.X <= reach && point.Y <= reach) return SpotifyOverlayDragMode.TopLeft;
        if (point.X >= size.Width - reach && point.Y <= reach) return SpotifyOverlayDragMode.TopRight;
        if (point.X <= reach && point.Y >= size.Height - reach) return SpotifyOverlayDragMode.BottomLeft;
        if (point.X >= size.Width - reach && point.Y >= size.Height - reach) return SpotifyOverlayDragMode.BottomRight;
        return SpotifyOverlayDragMode.Move;
    }
}
