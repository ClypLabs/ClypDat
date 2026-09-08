using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public readonly record struct SpotifyOverlayBounds(int X, int Y, int Width, int Height);
public readonly record struct SpotifyOverlayProjection(SpotifyOverlayBounds CardBounds, SpotifyOverlayBounds VisibleBounds);

/// <summary>The same output-relative card geometry for editor, trim, export and share.</summary>
public static class SpotifyOverlayLayout
{
    public const double AspectRatio = 406.0 / 140;
    public const double MinimumWidth = .05;

    public static SpotifyOverlayProjection Project(SpotifyOverlayBounds frameBounds, SpotifyOverlayBounds viewportBounds,
        string? position, SpotifyOverlayTransform? transform = null)
    {
        // Zoom/pan change which part of the output is visible, never the
        // normalized coordinates of the output or its independently edited card.
        var local = ResolveRenderBounds(frameBounds.Width, frameBounds.Height, position, transform);
        var card = local with { X = frameBounds.X + local.X, Y = frameBounds.Y + local.Y };
        var left = Math.Max(card.X, viewportBounds.X);
        var top = Math.Max(card.Y, viewportBounds.Y);
        var right = Math.Min(card.X + card.Width, viewportBounds.X + Math.Max(0, viewportBounds.Width));
        var bottom = Math.Min(card.Y + card.Height, viewportBounds.Y + Math.Max(0, viewportBounds.Height));
        return new(card, new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)));
    }

    public static SpotifyOverlayTransform Normalize(int frameWidth, int frameHeight, SpotifyOverlayTransform transform)
    {
        frameWidth = Math.Max(1, frameWidth);
        frameHeight = Math.Max(1, frameHeight);
        var maximumWidth = Math.Min(1, frameHeight * AspectRatio / frameWidth);
        var defaultWidth = SpotifyOverlayCardRenderer.SourceWidth * SpotifyOverlayCardRenderer.Scale(frameWidth, frameHeight) / frameWidth;
        var width = Math.Clamp(Finite(transform.Width, defaultWidth), Math.Min(MinimumWidth, maximumWidth), maximumWidth);
        var height = width * frameWidth / AspectRatio / frameHeight;
        // Position remains bounded for the unrotated card. Rotation is allowed
        // to extend past the picture and is clipped by the editor/export frame.
        var rotation = Finite(transform.RotationDegrees, 0) % 360;
        if (rotation <= -180) rotation += 360;
        if (rotation > 180) rotation -= 360;
        return new(Math.Clamp(Finite(transform.X, 0), 0, 1 - width), Math.Clamp(Finite(transform.Y, 0), 0, Math.Max(0, 1 - height)), width, rotation);
    }

    public static SpotifyOverlayBounds Resolve(int frameWidth, int frameHeight, string? position, SpotifyOverlayTransform? transform = null)
    {
        frameWidth = Math.Max(1, frameWidth);
        frameHeight = Math.Max(1, frameHeight);
        if (transform is not null)
        {
            var normalized = Normalize(frameWidth, frameHeight, transform);
            var exactWidth = normalized.Width * frameWidth;
            var width = Math.Clamp(CeilingPixel(exactWidth), 1, frameWidth);
            var height = Math.Clamp(CeilingPixel(exactWidth / AspectRatio), 1, frameHeight);
            return new(Math.Clamp(Round(normalized.X * frameWidth), 0, frameWidth - width),
                Math.Clamp(Round(normalized.Y * frameHeight), 0, frameHeight - height), width, height);
        }

        var scale = SpotifyOverlayCardRenderer.Scale(frameWidth, frameHeight);
        var cardWidth = Math.Clamp(CeilingPixel(406 * scale), 1, frameWidth);
        var cardHeight = Math.Clamp(CeilingPixel(140 * scale), 1, frameHeight);
        var x = position?.EndsWith("Right", StringComparison.OrdinalIgnoreCase) == true ? frameWidth - cardWidth : 0;
        var inset = Round(frameHeight * 14.0 / 1080);
        var y = position?.StartsWith("Top", StringComparison.OrdinalIgnoreCase) == true ? inset :
            position?.StartsWith("Center", StringComparison.OrdinalIgnoreCase) == true ? Round((frameHeight - cardHeight) / 2.0) : frameHeight - cardHeight - inset;
        return new(x, Math.Clamp(y, 0, frameHeight - cardHeight), cardWidth, cardHeight);
    }

    /// <summary>Transparent raster bounds needed after rotating an editable card.</summary>
    public static SpotifyOverlayBounds ResolveRenderBounds(int frameWidth, int frameHeight, string? position, SpotifyOverlayTransform? transform = null)
    {
        var card = Resolve(frameWidth, frameHeight, position, transform);
        var degrees = transform is null ? 0 : Normalize(frameWidth, frameHeight, transform).RotationDegrees;
        if (Math.Abs(degrees) < .001) return card;
        var radians = degrees * Math.PI / 180;
        var width = (int)Math.Ceiling(Math.Abs(card.Width * Math.Cos(radians)) + Math.Abs(card.Height * Math.Sin(radians)));
        var height = (int)Math.Ceiling(Math.Abs(card.Width * Math.Sin(radians)) + Math.Abs(card.Height * Math.Cos(radians)));
        return new SpotifyOverlayBounds(
            (int)Math.Floor(card.X + (card.Width - width) / 2.0),
            (int)Math.Floor(card.Y + (card.Height - height) / 2.0), Math.Max(1, width), Math.Max(1, height));
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
    // Pixel -> normalized -> pixel can produce 331.00000000000006. Do not
    // enlarge the card by one pixel every time a drag stores that same width.
    private static int CeilingPixel(double value) => (int)Math.Ceiling(value - .00000001);
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
