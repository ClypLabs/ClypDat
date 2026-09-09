using Avalonia;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Which part of a captured camera or keyboard overlay a pointer landed on.
/// Corner reach matches the Spotify adorner's, so every overlay in the editor
/// grabs identically regardless of which one it is.
/// </summary>
internal static class CapturedOverlayHitTest
{
    /// <summary>Same reach as <c>SpotifyOverlayAdorner.HandleSize + 4</c>.</summary>
    public const double HandleReach = 14;

    public static bool TryHit(Rect bounds, Point point, out VideoOverlayManipulationMode mode)
    {
        mode = VideoOverlayManipulationMode.Move;
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        // A corner wins over the body it overlaps, so resizing stays reachable
        // on an overlay small enough for its handles to cover most of it.
        var left = Math.Abs(point.X - bounds.Left) <= HandleReach;
        var right = Math.Abs(point.X - bounds.Right) <= HandleReach;
        var top = Math.Abs(point.Y - bounds.Top) <= HandleReach;
        var bottom = Math.Abs(point.Y - bounds.Bottom) <= HandleReach;
        var withinX = point.X >= bounds.Left - HandleReach && point.X <= bounds.Right + HandleReach;
        var withinY = point.Y >= bounds.Top - HandleReach && point.Y <= bounds.Bottom + HandleReach;
        if (withinX && withinY)
        {
            if (left && top) { mode = VideoOverlayManipulationMode.TopLeft; return true; }
            if (right && top) { mode = VideoOverlayManipulationMode.TopRight; return true; }
            if (left && bottom) { mode = VideoOverlayManipulationMode.BottomLeft; return true; }
            if (right && bottom) { mode = VideoOverlayManipulationMode.BottomRight; return true; }
        }
        if (bounds.Contains(point)) { mode = VideoOverlayManipulationMode.Move; return true; }
        return false;
    }
}
