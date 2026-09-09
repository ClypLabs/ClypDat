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
    /// <summary>Captured camera and keyboard layers store no rotation
    /// (VideoOverlayTransform is x/y/width only), so they draw and hit-test
    /// without the rotation handle.</summary>
    public bool ShowRotationHandle { get; set; } = true;
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
            if (!ShowRotationHandle) return;
            var handle = RotationHandle(bounds);
            context.DrawLine(Selection, new Point(center.X, handle.Y < center.Y ? bounds.Top : bounds.Bottom), handle);
            context.DrawEllipse(Brushes.White, HandleOutline, handle, HandleSize / 2, HandleSize / 2);
        }
    }

    public SpotifyOverlayDragMode HitTest(Point point)
        => TryHitTest(point, out var mode) ? mode : SpotifyOverlayDragMode.Move;

    /// <summary>Finds a card gesture target without treating empty canvas as a move.</summary>
    public bool TryHitTest(Point point, out SpotifyOverlayDragMode mode)
    {
        var center = CardBounds.Center;
        var radians = -RotationDegrees * Math.PI / 180;
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        var local = new Point(center.X + dx * Math.Cos(radians) - dy * Math.Sin(radians), center.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
        var rotation = RotationHandle(CardBounds);
        var rotationDx = local.X - rotation.X;
        var rotationDy = local.Y - rotation.Y;
        if (ShowRotationHandle && rotationDx * rotationDx + rotationDy * rotationDy <= (HandleSize + 3) * (HandleSize + 3))
        {
            mode = SpotifyOverlayDragMode.Rotate;
            return true;
        }
        var reach = HandleSize + 4;
        if (Math.Abs(local.X - CardBounds.Left) <= reach && Math.Abs(local.Y - CardBounds.Top) <= reach) mode = SpotifyOverlayDragMode.TopLeft;
        else if (Math.Abs(local.X - CardBounds.Right) <= reach && Math.Abs(local.Y - CardBounds.Top) <= reach) mode = SpotifyOverlayDragMode.TopRight;
        else if (Math.Abs(local.X - CardBounds.Left) <= reach && Math.Abs(local.Y - CardBounds.Bottom) <= reach) mode = SpotifyOverlayDragMode.BottomLeft;
        else if (Math.Abs(local.X - CardBounds.Right) <= reach && Math.Abs(local.Y - CardBounds.Bottom) <= reach) mode = SpotifyOverlayDragMode.BottomRight;
        else if (CardBounds.Contains(local)) mode = SpotifyOverlayDragMode.Move;
        else
        {
            mode = default;
            return false;
        }
        return true;
    }

    // At a frame edge keep rotation grab inside card, not clipped outside host.
    private static Point RotationHandle(Rect bounds) => new(bounds.Center.X,
        bounds.Top - RotationHandleOffset < HandleSize ? bounds.Top + RotationHandleOffset : bounds.Top - RotationHandleOffset);
}
