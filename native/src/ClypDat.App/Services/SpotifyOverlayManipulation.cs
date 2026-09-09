using Avalonia;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

internal enum SpotifyOverlayDragMode { Move, TopLeft, TopRight, BottomLeft, BottomRight, Rotate }

internal static class SpotifyOverlayManipulation
{
    public static SpotifyOverlayTransform Apply(SpotifyOverlayTransform start, SpotifyOverlayDragMode mode,
        double deltaX, double deltaY, int frameWidth, int frameHeight)
    {
        start = SpotifyOverlayLayout.Normalize(frameWidth, frameHeight, start);
        if (mode == SpotifyOverlayDragMode.Move)
            return SpotifyOverlayLayout.Normalize(frameWidth, frameHeight,
                start with { X = start.X + deltaX / frameWidth, Y = start.Y + deltaY / frameHeight });

        if (mode == SpotifyOverlayDragMode.Rotate) return start;
        var right = mode is SpotifyOverlayDragMode.TopRight or SpotifyOverlayDragMode.BottomRight;
        var bottom = mode is SpotifyOverlayDragMode.BottomLeft or SpotifyOverlayDragMode.BottomRight;
        var aspect = 1 / SpotifyOverlayLayout.AspectRatio;
        var initialWidth = start.Width * frameWidth;
        var angle = start.RotationDegrees * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        // Work in card axes. Pointer deltas arrive in output axes.
        var localX = deltaX * cos + deltaY * sin;
        var localY = -deltaX * sin + deltaY * cos;
        var centerX = (start.X + start.Width / 2) * frameWidth;
        var centerY = (start.Y + start.Width * frameWidth / SpotifyOverlayLayout.AspectRatio / frameHeight / 2) * frameHeight;
        var oldCornerX = (right ? initialWidth / 2 : -initialWidth / 2);
        var oldCornerY = (bottom ? initialWidth * aspect / 2 : -initialWidth * aspect / 2);
        var anchorX = centerX - (oldCornerX * cos - oldCornerY * sin);
        var anchorY = centerY - (oldCornerX * sin + oldCornerY * cos);
        // Project the pointer onto the diagonal, keeping the opposite corner
        // stationary and the artwork/text proportions intact.
        var deltaWidth = ((right ? localX : -localX) + (bottom ? localY : -localY) * aspect) / (1 + aspect * aspect);
        // Normalize clamps the unrotated bounds. Keep maximum broad here so
        // rotated opposite corners remain stationary except at frame limits.
        var width = Math.Max(frameWidth * SpotifyOverlayLayout.MinimumWidth, initialWidth + deltaWidth);
        width = Math.Min(width, Math.Min(frameWidth, frameHeight * SpotifyOverlayLayout.AspectRatio));
        var cornerX = right ? width / 2 : -width / 2;
        var cornerY = bottom ? width * aspect / 2 : -width * aspect / 2;
        var newCenterX = anchorX + (cornerX * cos - cornerY * sin);
        var newCenterY = anchorY + (cornerX * sin + cornerY * cos);
        return SpotifyOverlayLayout.Normalize(frameWidth, frameHeight, new(
            (newCenterX - width / 2) / frameWidth,
            (newCenterY - width * aspect / 2) / frameHeight,
            width / frameWidth, start.RotationDegrees));
    }

    public static SpotifyOverlayTransform Rotate(SpotifyOverlayTransform start, Point pointerStart, Point pointerNow,
        int frameWidth, int frameHeight)
    {
        start = SpotifyOverlayLayout.Normalize(frameWidth, frameHeight, start);
        var center = new Point((start.X + start.Width / 2) * frameWidth,
            (start.Y + start.Width * frameWidth / SpotifyOverlayLayout.AspectRatio / frameHeight / 2) * frameHeight);
        var fromX = pointerStart.X - center.X;
        var fromY = pointerStart.Y - center.Y;
        var toX = pointerNow.X - center.X;
        var toY = pointerNow.Y - center.Y;
        if (fromX * fromX + fromY * fromY < 1 || toX * toX + toY * toY < 1) return start;
        var degrees = start.RotationDegrees + (Math.Atan2(toY, toX) - Math.Atan2(fromY, fromX)) * 180 / Math.PI;
        var snapped = Math.Round(degrees / 15) * 15;
        if (Math.Abs(degrees - snapped) <= 3) degrees = snapped;
        return SpotifyOverlayLayout.Normalize(frameWidth, frameHeight, start with { RotationDegrees = degrees });
    }
}
