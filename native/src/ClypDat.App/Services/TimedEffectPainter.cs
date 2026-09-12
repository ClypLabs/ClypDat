using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Draws text and blur effects. Export rasterizes text through the same
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
    /// Crops <paramref name="region"/> out of a BGRA frame and blurs it the way
    /// FFmpeg's <c>crop,gblur</c> does on export: edges are clamped, so pixels
    /// outside the region never bleed in. Three box passes approximate the
    /// gaussian closely enough for a region that is blurred beyond recognition.
    /// </summary>
    public static byte[] BlurRegion(byte[] frame, int width, int height, PixelRect region, double sigma)
    {
        var x = Math.Clamp(region.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(region.Y, 0, Math.Max(0, height - 1));
        var w = Math.Clamp(region.Width, 1, width - x);
        var h = Math.Clamp(region.Height, 1, height - y);
        var a = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(frame, ((y + row) * width + x) * 4, a, row * w * 4, w * 4);
        if (sigma < .5) return a;
        var b = new byte[a.Length];
        foreach (var box in BoxSizes(sigma, 3))
        {
            var r = (box - 1) / 2;
            BoxHorizontal(a, b, w, h, r);
            BoxVertical(b, a, w, h, r);
        }
        return a;
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
}
