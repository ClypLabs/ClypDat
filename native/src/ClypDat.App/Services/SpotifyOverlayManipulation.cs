using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

internal enum SpotifyOverlayDragMode { Move, TopLeft, TopRight, BottomLeft, BottomRight }

internal static class SpotifyOverlayManipulation
{
    public static SpotifyOverlayTransform Apply(SpotifyOverlayTransform start, SpotifyOverlayDragMode mode,
        double deltaX, double deltaY, int frameWidth, int frameHeight)
    {
        start = SpotifyOverlayLayout.Normalize(frameWidth, frameHeight, start);
        if (mode == SpotifyOverlayDragMode.Move)
            return SpotifyOverlayLayout.Normalize(frameWidth, frameHeight,
                start with { X = start.X + deltaX / frameWidth, Y = start.Y + deltaY / frameHeight });

        var right = mode is SpotifyOverlayDragMode.TopRight or SpotifyOverlayDragMode.BottomRight;
        var bottom = mode is SpotifyOverlayDragMode.BottomLeft or SpotifyOverlayDragMode.BottomRight;
        var aspect = 1 / SpotifyOverlayLayout.AspectRatio;
        var initialWidth = start.Width * frameWidth;
        var anchorX = start.X * frameWidth + (right ? 0 : initialWidth);
        var anchorY = start.Y * frameHeight + (bottom ? 0 : initialWidth * aspect);
        // Project the pointer onto the diagonal, keeping the opposite corner
        // stationary and the artwork/text proportions intact.
        var deltaWidth = ((right ? deltaX : -deltaX) + (bottom ? deltaY : -deltaY) * aspect) / (1 + aspect * aspect);
        var maximum = Math.Min(right ? frameWidth - anchorX : anchorX, (bottom ? frameHeight - anchorY : anchorY) / aspect);
        var minimum = Math.Min(frameWidth * SpotifyOverlayLayout.MinimumWidth, maximum);
        var width = Math.Clamp(initialWidth + deltaWidth, minimum, maximum);
        return SpotifyOverlayLayout.Normalize(frameWidth, frameHeight, new(
            (right ? anchorX : anchorX - width) / frameWidth,
            (bottom ? anchorY : anchorY - width * aspect) / frameHeight,
            width / frameWidth));
    }
}
