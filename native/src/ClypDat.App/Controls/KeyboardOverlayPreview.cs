using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClypDat.Core.Settings;

namespace ClypDat.App.Controls;

/// <summary>Resolution-independent Medal-style keyboard and mouse preview.</summary>
public sealed class KeyboardOverlayPreview : Control
{
    public static readonly StyledProperty<string> LayoutProperty =
        AvaloniaProperty.Register<KeyboardOverlayPreview, string>(nameof(Layout), KeyboardOverlayCatalog.QwertyCompact);

    public string Layout { get => GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var definition = KeyboardOverlayCatalog.Get(Layout);
        var scale = Math.Min(Bounds.Width / definition.NativeWidth, Bounds.Height / definition.NativeHeight);
        if (scale <= 0) return;
        var origin = new Point((Bounds.Width - definition.NativeWidth * scale) / 2, (Bounds.Height - definition.NativeHeight * scale) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X, origin.Y)))
        {
            var pressed = definition.SamplePressed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            DrawKeyboard(context, definition, pressed);
            DrawMouse(context, definition, pressed);
        }
    }

    private static readonly IBrush Dark = Brush.Parse("#202329");
    private static readonly IBrush Blue = Brush.Parse("#184ED6");
    private static readonly IPen Outline = new Pen(Brushes.White, 5);
    private static readonly Typeface Typeface = new("Inter");

    private static void DrawKeyboard(DrawingContext context, KeyboardOverlayDefinition definition, HashSet<string> pressed)
    {
        if (definition.DisplayName == KeyboardOverlayCatalog.Arrows)
        {
            DrawKey(context, new Rect(20, 145, 125, 125), "←", pressed.Contains("Left"));
            DrawKey(context, new Rect(155, 10, 125, 125), "↑", pressed.Contains("Up"));
            DrawKey(context, new Rect(155, 145, 125, 125), "↓", pressed.Contains("Down"));
            DrawKey(context, new Rect(290, 145, 125, 125), "→", pressed.Contains("Right"));
            return;
        }

        var full = definition.DisplayName == KeyboardOverlayCatalog.QwertyFull;
        var x = 20d;
        var key = 82d;
        var gap = 12d;
        var rows = definition.DisplayName == KeyboardOverlayCatalog.AzertyCompact
            ? new[] { "AZERTYUIOP", "QSDFGHJKLM", "WXCVBN" }
            : new[] { "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
        if (full)
        {
            rows = definition.DisplayName == KeyboardOverlayCatalog.AzertyCompact
                ? new[] { "1234567890", "AZERTYUIOP", "QSDFGHJKLM", "WXCVBN" }
                : new[] { "1234567890", "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
            key = 78;
        }
        for (var row = 0; row < rows.Length; row++)
        {
            var offset = row == 1 ? key * .42 : row >= 2 ? key * .72 : 0;
            foreach (var label in rows[row].Select(letter => letter.ToString()))
            {
                DrawKey(context, new Rect(x + offset, 18 + row * (key + gap), key, key), label, pressed.Contains(label));
                offset += key + gap;
            }
        }
        var bottom = 18 + rows.Length * (key + gap);
        DrawKey(context, new Rect(x, bottom, key * 1.4, key), "Ctrl", false);
        DrawKey(context, new Rect(x + key * 1.55, bottom, key * 3.8, key), "Space", false);
        if (full)
            DrawKey(context, new Rect(x + key * 5.5, bottom, key * 1.3, key), "Alt", false);
    }

    private static void DrawMouse(DrawingContext context, KeyboardOverlayDefinition definition, HashSet<string> pressed)
    {
        var width = 190d;
        var height = Math.Min(360, definition.NativeHeight - 36d);
        var x = definition.NativeWidth - width - 20;
        var y = (definition.NativeHeight - height) / 2;
        var body = new RoundedRect(new Rect(x, y, width, height), 42);
        context.DrawRectangle(Dark, Outline, body);
        var left = new RoundedRect(new Rect(x + 9, y + 9, width / 2 - 14, height * .44), 34);
        var right = new RoundedRect(new Rect(x + width / 2 + 5, y + 9, width / 2 - 14, height * .44), 34);
        context.DrawRectangle(pressed.Contains("MouseLeft") ? Blue : Dark, Outline, left);
        context.DrawRectangle(pressed.Contains("MouseRight") ? Blue : Dark, Outline, right);
        context.DrawRectangle(Dark, Outline, new RoundedRect(new Rect(x + width * .39, y + height * .27, width * .22, height * .22), 10));
        context.DrawLine(new Pen(Brushes.White, 5), new Point(x + width / 2, y + 8), new Point(x + width / 2, y + height * .45));
    }

    private static void DrawKey(DrawingContext context, Rect bounds, string text, bool isPressed)
    {
        context.DrawRectangle(isPressed ? Blue : Dark, Outline, new RoundedRect(bounds, 11));
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, Math.Min(30, bounds.Height * .35), Brushes.White);
        context.DrawText(label, new Point(bounds.Center.X - label.Width / 2, bounds.Center.Y - label.Height / 2));
    }
}
