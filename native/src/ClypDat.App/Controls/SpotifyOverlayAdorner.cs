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

    public Rect CardBounds { get; set; }
    public double RotationDegrees { get; set; }
    public const double HandleSize = 10;
    public const double RotationHandleOffset = 24;

    public override void Render(DrawingContext context)
    {
        var bounds = CardBounds;
        if (bounds.Width < 3 || bounds.Height < 3) return;
        var center = bounds.Center;
        using (context.PushTransform(Matrix.CreateRotation(RotationDegrees * Math.PI / 180, center)))
        {
            context.DrawRectangle(null, Selection, bounds.Deflate(1));
            foreach (var x in new[] { bounds.Left, bounds.Right })
            foreach (var y in new[] { bounds.Top, bounds.Bottom })
                context.DrawRectangle(Brushes.White, HandleOutline, new Rect(x - HandleSize / 2, y - HandleSize / 2, HandleSize, HandleSize), 2, 2);
            var handle = RotationHandle(bounds);
            context.DrawLine(Selection, new Point(center.X, handle.Y < center.Y ? bounds.Top : bounds.Bottom), handle);
            context.DrawEllipse(Brushes.White, HandleOutline, handle, HandleSize / 2, HandleSize / 2);
        }
    }

    public SpotifyOverlayDragMode HitTest(Point point)
    {
        var center = CardBounds.Center;
        var radians = -RotationDegrees * Math.PI / 180;
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        var local = new Point(center.X + dx * Math.Cos(radians) - dy * Math.Sin(radians), center.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
        var rotation = RotationHandle(CardBounds);
        var rotationDx = local.X - rotation.X;
        var rotationDy = local.Y - rotation.Y;
        if (rotationDx * rotationDx + rotationDy * rotationDy <= (HandleSize + 3) * (HandleSize + 3)) return SpotifyOverlayDragMode.Rotate;
        var reach = HandleSize + 4;
        if (Math.Abs(local.X - CardBounds.Left) <= reach && Math.Abs(local.Y - CardBounds.Top) <= reach) return SpotifyOverlayDragMode.TopLeft;
        if (Math.Abs(local.X - CardBounds.Right) <= reach && Math.Abs(local.Y - CardBounds.Top) <= reach) return SpotifyOverlayDragMode.TopRight;
        if (Math.Abs(local.X - CardBounds.Left) <= reach && Math.Abs(local.Y - CardBounds.Bottom) <= reach) return SpotifyOverlayDragMode.BottomLeft;
        if (Math.Abs(local.X - CardBounds.Right) <= reach && Math.Abs(local.Y - CardBounds.Bottom) <= reach) return SpotifyOverlayDragMode.BottomRight;
        return SpotifyOverlayDragMode.Move;
    }

    // At a frame edge keep rotation grab inside card, not clipped outside host.
    private static Point RotationHandle(Rect bounds) => new(bounds.Center.X,
        bounds.Top - RotationHandleOffset < HandleSize ? bounds.Top + RotationHandleOffset : bounds.Top - RotationHandleOffset);
}
