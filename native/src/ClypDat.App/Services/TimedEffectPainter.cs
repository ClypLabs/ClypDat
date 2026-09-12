using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Draws text effects and blurs live blur patches. Export rasterizes text through the same
/// <see cref="DrawText"/> the editor overlay draws live with, so the preview and
/// the burned-in result cannot drift apart. Sizes scale with frame height: a
/// value is authored against 1080p and multiplied by frameHeight/1080.
/// </summary>
public static class TimedEffectPainter
{
    public static void DrawText(DrawingContext context, TimedVideoEffect e, Rect box, double frameHeight)
    {
        if (box.Width <= 0 || box.Height <= 0 || frameHeight <= 0) return;
        using (context.PushClip(box))
        {
            if (e.BackgroundOpacity > 0)
                context.DrawRectangle(new SolidColorBrush(ParseColour(e.Background, Colors.Black), e.BackgroundOpacity), null, box);
            if (string.IsNullOrEmpty(e.Text)) return;
            var typeface = new Typeface(new FontFamily(e.Font), weight: e.Bold ? FontWeight.Bold : FontWeight.Normal);
            var alignment = Enum.TryParse<TextAlignment>(e.Alignment, out var parsed) ? parsed : TextAlignment.Center;
            var size = Math.Max(1, e.FontSize * frameHeight / 1080);
            var radius = e.Outline * frameHeight / 1080;
            if (radius > 0)
            {
                using var outline = new TextLayout(e.Text, typeface, size, Brushes.Black, textAlignment: alignment,
                    textWrapping: TextWrapping.Wrap, maxWidth: box.Width, maxHeight: box.Height);
                for (var i = 0; i < 16; i++)
                    outline.Draw(context, box.TopLeft + new Vector(Math.Cos(i * Math.PI / 8) * radius, Math.Sin(i * Math.PI / 8) * radius));
            }
            using var text = new TextLayout(e.Text, typeface, size, new SolidColorBrush(ParseColour(e.Colour, Colors.White)), textAlignment: alignment,
                textWrapping: TextWrapping.Wrap, maxWidth: box.Width, maxHeight: box.Height);
            text.Draw(context, box.TopLeft);
        }
    }

    public static Color ParseColour(string? value, Color fallback) => Color.TryParse(value, out var colour) ? colour : fallback;

    /// <summary>
    /// How far to shrink a buffer before blurring it with sigma
    /// <paramref name="sigmaPixels"/>: the blur then runs at about three pixels of
    /// sigma, where a gaussian drawn back up at display size is indistinguishable
    /// from one computed at full size, on a buffer k² times smaller.
    /// </summary>
    public static int WorkingFactor(double sigmaPixels) => Math.Max(1, (int)Math.Floor(sigmaPixels / 3));

    /// <summary>Averages each k×k block into one pixel. Partial blocks at the
    /// right and bottom average whatever pixels they have.</summary>
    public static (byte[] Pixels, int Width, int Height) Downsample(byte[] bgra, int width, int height, int factor)
    {
        if (factor <= 1) return (bgra, width, height);
        var w = (width + factor - 1) / factor;
        var h = (height + factor - 1) / factor;
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var y0 = y * factor;
            var y1 = Math.Min(height, y0 + factor);
            for (var x = 0; x < w; x++)
            {
                var x0 = x * factor;
                var x1 = Math.Min(width, x0 + factor);
                int b = 0, g = 0, r = 0, a = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var i = (sy * width + x0) * 4;
                    for (var sx = x0; sx < x1; sx++, i += 4) { b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; a += bgra[i + 3]; }
                }
                var count = (y1 - y0) * (x1 - x0);
                var o = (y * w + x) * 4;
                result[o] = (byte)(b / count); result[o + 1] = (byte)(g / count); result[o + 2] = (byte)(r / count); result[o + 3] = (byte)(a / count);
            }
        }
        return (result, w, h);
    }

    /// <summary>
    /// Gaussian blur of a whole BGRA buffer, in place, as three box passes.
    /// Edges clamp; callers pass a buffer already padded with the picture
    /// around the region, so the clamp only ever touches pixels that get cut off.
    /// </summary>
    public static void Blur(byte[] bgra, int width, int height, double sigma)
    {
        if (sigma < .5 || width <= 0 || height <= 0) return;
        var scratch = new byte[bgra.Length];
        foreach (var box in BoxSizes(sigma, 3))
        {
            var r = (box - 1) / 2;
            BoxHorizontal(bgra, scratch, width, height, r);
            BoxVertical(scratch, bgra, width, height, r);
        }
    }

    private static int[] BoxSizes(double sigma, int passes)
    {
        var ideal = Math.Sqrt(12 * sigma * sigma / passes + 1);
        var lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0) lower--;
        var upper = lower + 2;
        var split = (int)Math.Round((12 * sigma * sigma - passes * lower * lower - 4 * passes * lower - 3 * passes) / (-4.0 * lower - 4));
        return Enumerable.Range(0, passes).Select(i => i < split ? lower : upper).ToArray();
    }

    private static void BoxHorizontal(byte[] source, byte[] target, int w, int h, int r)
    {
        if (r <= 0) { Buffer.BlockCopy(source, 0, target, 0, source.Length); return; }
        var span = 2 * r + 1;
        for (var y = 0; y < h; y++)
        {
            var row = y * w * 4;
            for (var c = 0; c < 4; c++)
            {
                var sum = 0;
                for (var k = -r; k <= r; k++) sum += source[row + Math.Clamp(k, 0, w - 1) * 4 + c];
                for (var x = 0; x < w; x++)
                {
                    target[row + x * 4 + c] = (byte)((sum + span / 2) / span);
                    sum += source[row + Math.Min(x + r + 1, w - 1) * 4 + c] - source[row + Math.Max(x - r, 0) * 4 + c];
                }
            }
        }
    }

    private static void BoxVertical(byte[] source, byte[] target, int w, int h, int r)
    {
        if (r <= 0) { Buffer.BlockCopy(source, 0, target, 0, source.Length); return; }
        var span = 2 * r + 1;
        var stride = w * 4;
        for (var x = 0; x < w; x++)
        {
            for (var c = 0; c < 4; c++)
            {
                var column = x * 4 + c;
                var sum = 0;
                for (var k = -r; k <= r; k++) sum += source[Math.Clamp(k, 0, h - 1) * stride + column];
                for (var y = 0; y < h; y++)
                {
                    target[y * stride + column] = (byte)((sum + span / 2) / span);
                    sum += source[Math.Min(y + r + 1, h - 1) * stride + column] - source[Math.Max(y - r, 0) * stride + column];
                }
            }
        }
    }

    /// <summary>
    /// Outline of a blur inside its box, or null for a plain rectangle. The
    /// live clip, the on-video outline and the export mask all come from here,
    /// so the three cannot disagree. Rounded corners are a fifth of the short side.
    /// </summary>
    public static Geometry? BlurShape(string? shape, Rect box) => shape switch
    {
        "Ellipse" => new EllipseGeometry(box),
        "Rounded" => new RectangleGeometry(box, Math.Min(box.Width, box.Height) / 5, Math.Min(box.Width, box.Height) / 5),
        _ => null
    };
}
