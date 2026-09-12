using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Draws text effects. Export rasterizes text through the same
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
}
