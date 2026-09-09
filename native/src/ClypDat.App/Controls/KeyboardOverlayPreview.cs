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

    static KeyboardOverlayPreview() => AffectsRender<KeyboardOverlayPreview>(LayoutProperty);

    // Every distance below is a multiple of one key, so a layout describes
    // itself in keys and the canvas decides how big a key is. The old code
    // hard-coded 82px keys against Medal's canvases, which is why the full
    // board covered half of its 1989x540 canvas and left the mouse marooned on
    // the far side of the gap.
    private const double Gap = .14;         // between keys
    private const double MouseGap = .45;    // between the board and the mouse
    private const double ClusterGap = .3;   // between the board and the arrow cluster
    private const double MouseAspect = .53; // mouse width, relative to its height
    private const double CanvasPadding = 18;       // canvas edge, in canvas pixels

    private sealed record Key(string Label, double Units = 1);
    private sealed record Row(double Offset, IReadOnlyList<Key> Keys);
    private sealed record Board(IReadOnlyList<Row> Rows, IReadOnlyList<Row>? Cluster = null);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var definition = KeyboardOverlayCatalog.Get(Layout);
        var canvasScale = Math.Min(Bounds.Width / definition.NativeWidth, Bounds.Height / definition.NativeHeight);
        if (canvasScale <= 0) return;
        var canvasOrigin = new Point(
            (Bounds.Width - definition.NativeWidth * canvasScale) / 2,
            (Bounds.Height - definition.NativeHeight * canvasScale) / 2);

        var board = Describe(definition.DisplayName);
        var boardWidth = UnitWidth(board.Rows);
        var boardHeight = UnitHeight(board.Rows);
        var clusterWidth = board.Cluster is null ? 0 : ClusterGap + UnitWidth(board.Cluster);
        var mouseWidth = MouseAspect * boardHeight;
        var totalWidth = boardWidth + clusterWidth + MouseGap + mouseWidth;

        var key = Math.Min(
            (definition.NativeWidth - CanvasPadding * 2) / totalWidth,
            (definition.NativeHeight - CanvasPadding * 2) / boardHeight);
        if (key <= 0) return;

        var left = (definition.NativeWidth - totalWidth * key) / 2;
        var top = (definition.NativeHeight - boardHeight * key) / 2;
        var pressed = definition.SamplePressed.ToHashSet(StringComparer.OrdinalIgnoreCase);

        using (context.PushTransform(Matrix.CreateScale(canvasScale, canvasScale) * Matrix.CreateTranslation(canvasOrigin.X, canvasOrigin.Y)))
        {
            DrawRows(context, board.Rows, left, top, key, pressed);
            if (board.Cluster is not null)
                DrawRows(context, board.Cluster, left + (boardWidth + ClusterGap) * key,
                    top + (boardHeight - UnitHeight(board.Cluster)) * key, key, pressed);
            DrawMouse(context, new Rect(
                left + (boardWidth + clusterWidth + MouseGap) * key, top,
                mouseWidth * key, boardHeight * key), pressed);
        }
    }

    private static readonly IBrush Dark = Brush.Parse("#202329");
    private static readonly IBrush Blue = Brush.Parse("#184ED6");
    private static readonly Typeface Typeface = new("Inter");

    private static Board Describe(string layout) => layout switch
    {
        // A 60% board plus its arrow cluster. Anything narrower leaves Medal's
        // 1989-wide canvas half empty, which is what "Full" was doing.
        KeyboardOverlayCatalog.QwertyFull => new Board(
        [
            new(0, [new("`"), .. Letters("1234567890"), new("-"), new("="), new("Bksp", 2)]),
            new(0, [new("Tab", 1.5), .. Letters("QWERTYUIOP"), new("["), new("]"), new("\\", 1.5)]),
            new(0, [new("Caps", 1.75), .. Letters("ASDFGHJKL"), new(";"), new("’"), new("Enter", 2.25)]),
            new(0, [new("Shift", 2.25), .. Letters("ZXCVBNM"), new(","), new("."), new("/"), new("Shift", 2.75)]),
            new(0, [new("Ctrl", 1.25), new("Win", 1.25), new("Alt", 1.25), new("Space", 7.5), new("Alt", 1.25), new("Fn", 1.25), new("Ctrl", 1.25)])
        ],
        [
            new(1 + Gap, [new("↑")]),
            new(0, [new("←"), new("↓"), new("→")])
        ]),
        KeyboardOverlayCatalog.Arrows => new Board(
        [
            new(1 + Gap, [new("↑")]),
            new(0, [new("←"), new("↓"), new("→")])
        ]),
        KeyboardOverlayCatalog.AzertyCompact => Truncated("AZERTYUIOP", "QSDFGHJKLM", "WXCVBN"),
        _ => Truncated("QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM")
    };

    // The truncated boards keep the staggered rows of a real keyboard without
    // the modifier columns, so the letters stay legible at overlay size.
    private static Board Truncated(string top, string home, string bottom) => new(
    [
        new(0, Letters(top)),
        new(.42, Letters(home)),
        new(.72, Letters(bottom)),
        new(0, [new("Ctrl", 1.4), new("Space", 3.8)])
    ]);

    private static IReadOnlyList<Key> Letters(string letters) =>
        letters.Select(letter => new Key(letter.ToString())).ToArray();

    private static double Span(double units) => units + (units - 1) * Gap;
    private static double UnitWidth(IReadOnlyList<Row> rows) => rows.Max(row =>
        row.Offset + row.Keys.Sum(key => Span(key.Units)) + Gap * (row.Keys.Count - 1));
    private static double UnitHeight(IReadOnlyList<Row> rows) => rows.Count + Gap * (rows.Count - 1);

    private static void DrawRows(DrawingContext context, IReadOnlyList<Row> rows, double left, double top, double key, HashSet<string> pressed)
    {
        for (var row = 0; row < rows.Count; row++)
        {
            var x = left + rows[row].Offset * key;
            var y = top + row * (1 + Gap) * key;
            foreach (var cap in rows[row].Keys)
            {
                var width = Span(cap.Units) * key;
                DrawKey(context, new Rect(x, y, width, key), cap.Label, pressed.Contains(cap.Label));
                x += width + Gap * key;
            }
        }
    }

    private static void DrawMouse(DrawingContext context, Rect bounds, HashSet<string> pressed)
    {
        // The buttons sit under the shell's own curve rather than floating as
        // two pills, and the wheel is a capsule in the split instead of a line
        // drawn across both of them.
        var outline = new Pen(Brushes.White, Math.Max(3, bounds.Width * .028));
        var shellRadius = bounds.Width * .48;
        context.DrawRectangle(Dark, outline, new RoundedRect(bounds,
            new CornerRadius(shellRadius, shellRadius, bounds.Width * .36, bounds.Width * .36)));

        var inset = bounds.Width * .08;
        var split = bounds.Width * .06;
        var buttonHeight = bounds.Height * .44;
        var buttonWidth = (bounds.Width - inset * 2 - split) / 2;
        var buttonRadius = shellRadius - inset;
        var innerRadius = bounds.Width * .06;
        context.DrawRectangle(pressed.Contains("MouseLeft") ? Blue : Dark, outline,
            new RoundedRect(new Rect(bounds.X + inset, bounds.Y + inset, buttonWidth, buttonHeight),
                new CornerRadius(buttonRadius, innerRadius, innerRadius, innerRadius)));
        context.DrawRectangle(pressed.Contains("MouseRight") ? Blue : Dark, outline,
            new RoundedRect(new Rect(bounds.Right - inset - buttonWidth, bounds.Y + inset, buttonWidth, buttonHeight),
                new CornerRadius(innerRadius, buttonRadius, innerRadius, innerRadius)));

        var wheelWidth = bounds.Width * .12;
        var wheelHeight = buttonHeight * .52;
        context.DrawRectangle(pressed.Contains("MouseMiddle") ? Blue : Dark, outline,
            new RoundedRect(new Rect(bounds.Center.X - wheelWidth / 2, bounds.Y + inset + buttonHeight * .18, wheelWidth, wheelHeight), wheelWidth / 2));
    }

    private static void DrawKey(DrawingContext context, Rect bounds, string text, bool isPressed)
    {
        context.DrawRectangle(isPressed ? Blue : Dark, new Pen(Brushes.White, Math.Max(2, bounds.Height * .055)),
            new RoundedRect(bounds, bounds.Height * .13));
        var label = Label(text, bounds.Height * .38);
        // "Shift" in a 2.25u key still has to fit; single letters keep the size.
        var available = bounds.Width - bounds.Height * .28;
        if (label.Width > available && label.Width > 0) label = Label(text, bounds.Height * .38 * available / label.Width);
        context.DrawText(label, new Point(bounds.Center.X - label.Width / 2, bounds.Center.Y - label.Height / 2));
    }

    private static FormattedText Label(string text, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, size, Brushes.White);
}
