using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public enum TimedEffectHandle { None, Move, North, South, East, West, NorthWest, NorthEast, SouthWest, SouthEast }

/// <summary>Normalized frame positions of the snap guides a gesture landed on.</summary>
public readonly record struct TimedEffectGuides(double? X, double? Y);

/// <summary>
/// Geometry of on-video text and blur gestures, in normalized frame units
/// (0..1 of the output frame). Pure so the drag math is testable without a UI.
/// </summary>
public static class TimedEffectManipulation
{
    public const double MinimumSize = .02;
    private static readonly double[] SnapTargets = [0, .5, 1];

    /// <summary>
    /// Applies a drag of (dx, dy) from the gesture's start state. Text corners
    /// scale box and font together, the way a title scales in an NLE; every
    /// other handle resizes the box. snapX/snapY are the snap distance in
    /// normalized units (zero disables snapping).
    /// </summary>
    public static (TimedVideoEffect Effect, TimedEffectGuides Guides) Apply(TimedVideoEffect start, TimedEffectHandle handle,
        double dx, double dy, bool text, double snapX, double snapY)
    {
        switch (handle)
        {
            case TimedEffectHandle.None:
                return (start, default);
            case TimedEffectHandle.Move:
            {
                var (x, guideX) = SnapSpan(start.X + dx, start.Width, snapX);
                var (y, guideY) = SnapSpan(start.Y + dy, start.Height, snapY);
                return (start with { X = Math.Clamp(x, 0, 1 - start.Width), Y = Math.Clamp(y, 0, 1 - start.Height) }, new(guideX, guideY));
            }
            case TimedEffectHandle.NorthWest or TimedEffectHandle.NorthEast or TimedEffectHandle.SouthWest or TimedEffectHandle.SouthEast when text:
                return (Scale(start, handle, dx, dy), default);
        }

        var left = start.X;
        var top = start.Y;
        var right = start.X + start.Width;
        var bottom = start.Y + start.Height;
        double? gx = null, gy = null;
        if (handle is TimedEffectHandle.West or TimedEffectHandle.NorthWest or TimedEffectHandle.SouthWest)
            (left, gx) = SnapEdge(Math.Clamp(left + dx, 0, right - MinimumSize), snapX, 0, right - MinimumSize);
        if (handle is TimedEffectHandle.East or TimedEffectHandle.NorthEast or TimedEffectHandle.SouthEast)
            (right, gx) = SnapEdge(Math.Clamp(right + dx, left + MinimumSize, 1), snapX, left + MinimumSize, 1);
        if (handle is TimedEffectHandle.North or TimedEffectHandle.NorthWest or TimedEffectHandle.NorthEast)
            (top, gy) = SnapEdge(Math.Clamp(top + dy, 0, bottom - MinimumSize), snapY, 0, bottom - MinimumSize);
        if (handle is TimedEffectHandle.South or TimedEffectHandle.SouthWest or TimedEffectHandle.SouthEast)
            (bottom, gy) = SnapEdge(Math.Clamp(bottom + dy, top + MinimumSize, 1), snapY, top + MinimumSize, 1);
        return (start with { X = left, Y = top, Width = right - left, Height = bottom - top }, new(gx, gy));
    }

    /// <summary>Uniform scale about the opposite corner. The factor is bounded by
    /// the frame, the minimum box size and the 8-300 font range, so the result
    /// always validates.</summary>
    public static TimedVideoEffect Scale(TimedVideoEffect start, TimedEffectHandle corner, double dx, double dy)
    {
        var east = corner is TimedEffectHandle.NorthEast or TimedEffectHandle.SouthEast;
        var south = corner is TimedEffectHandle.SouthWest or TimedEffectHandle.SouthEast;
        var anchorX = east ? start.X : start.X + start.Width;
        var anchorY = south ? start.Y : start.Y + start.Height;
        var factor = ((start.Width + (east ? dx : -dx)) / start.Width + (start.Height + (south ? dy : -dy)) / start.Height) / 2;
        var roomX = east ? 1 - anchorX : anchorX;
        var roomY = south ? 1 - anchorY : anchorY;
        var max = Math.Min(Math.Min(roomX / start.Width, roomY / start.Height), 300 / start.FontSize);
        var min = Math.Max(Math.Max(MinimumSize / start.Width, MinimumSize / start.Height), 8 / start.FontSize);
        factor = min > max ? 1 : Math.Clamp(factor, min, max);
        var width = Math.Min(start.Width * factor, roomX);
        var height = Math.Min(start.Height * factor, roomY);
        return start with
        {
            Width = width,
            Height = height,
            X = east ? anchorX : anchorX - width,
            Y = south ? anchorY : anchorY - height,
            FontSize = Math.Clamp(start.FontSize * factor, 8, 300)
        };
    }

    /// <summary>Snaps a box's leading edge, centre or trailing edge to the frame
    /// edges or centre, whichever lands closest within the threshold.</summary>
    public static (double Position, double? Guide) SnapSpan(double position, double size, double threshold)
    {
        if (threshold <= 0) return (position, null);
        var best = threshold;
        double? guide = null;
        var snapped = position;
        foreach (var offset in new[] { 0, size / 2, size })
        foreach (var target in SnapTargets)
        {
            var distance = Math.Abs(position + offset - target);
            if (distance >= best) continue;
            best = distance;
            guide = target;
            snapped = target - offset;
        }
        return (snapped, guide);
    }

    private static (double Value, double? Guide) SnapEdge(double value, double threshold, double min, double max)
    {
        foreach (var target in SnapTargets)
            if (threshold > 0 && Math.Abs(value - target) < threshold && target >= min && target <= max) return (target, target);
        return (value, null);
    }

    /// <summary>
    /// Moves or trims an effect in time. edge: -1 start, 1 end, 0 whole clip.
    /// Edges snap to any of <paramref name="snapTimes"/> within
    /// <paramref name="snapSeconds"/>.
    /// </summary>
    public static TimedVideoEffect ApplyTime(TimedVideoEffect start, int edge, double deltaSeconds, double duration,
        IReadOnlyList<double> snapTimes, double snapSeconds)
    {
        const double minimum = .1;
        duration = Math.Max(duration, start.End);
        (double Value, double Distance) Snap(double value)
        {
            var best = snapSeconds;
            var result = value;
            foreach (var time in snapTimes)
            {
                var distance = Math.Abs(time - value);
                if (distance < best) { best = distance; result = time; }
            }
            return (result, result == value ? double.MaxValue : best);
        }
        if (edge < 0) return start with { Start = Math.Clamp(Snap(start.Start + deltaSeconds).Value, 0, start.End - minimum) };
        if (edge > 0) return start with { End = Math.Clamp(Snap(start.End + deltaSeconds).Value, start.Start + minimum, duration) };
        var length = start.End - start.Start;
        var moved = start.Start + deltaSeconds;
        var head = Snap(moved);
        var tail = Snap(moved + length);
        if (head.Distance < double.MaxValue || tail.Distance < double.MaxValue)
            moved = head.Distance <= tail.Distance ? head.Value : tail.Value - length;
        moved = Math.Clamp(moved, 0, Math.Max(0, duration - length));
        return start with { Start = moved, End = moved + length };
    }
}
