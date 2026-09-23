using ClypDat.Core.Settings;
using SkiaSharp;

namespace ClypDat.App.Services;

internal sealed class RecorderKeyboardRasterizer : IDisposable
{
    private SKBitmap? _bitmap; private byte[]? _pixels; private string? _signature;
    public byte[] Render(string layout, CustomKeyboardBoardShape? custom, int width, int height, IReadOnlySet<string> pressed)
    {
        var sig = $"{layout}|{width}|{height}|{custom}|{string.Join(',', pressed.Order(StringComparer.OrdinalIgnoreCase))}"; if (_pixels is not null && sig == _signature) return _pixels;
        _bitmap?.Dispose(); _bitmap = new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul); using var c = new SKCanvas(_bitmap); c.Clear(SKColors.Transparent);
        var board = custom is null ? KeyboardOverlayGeometry.Describe(layout) : KeyboardOverlayGeometry.FromCustom(custom); var bw = UnitWidth(board.Rows); var bh = Math.Max(UnitHeight(board.Rows), board.IncludeMouse ? KeyboardOverlayGeometry.MouseOnlyHeight : 0); if (board.Cluster is not null) bh = Math.Max(bh, UnitHeight(board.Cluster)); var cw = board.Cluster is null ? 0 : KeyboardOverlayGeometry.ClusterGap + UnitWidth(board.Cluster); var mw = board.IncludeMouse ? KeyboardOverlayGeometry.MouseAspect * bh : 0; var total = bw + cw + (board.IncludeMouse ? KeyboardOverlayGeometry.MouseGap + mw : 0); if (total <= 0 || bh <= 0) return Cache(sig);
        var k = Math.Min((width - 12d) / total, (height - 12d) / bh); var left = (float)((width - total * k) / 2); var top = (float)((height - bh * k) / 2); DrawRows(c, board.Rows, left, top, (float)k, pressed); if (board.Cluster is not null) DrawRows(c, board.Cluster, left + (float)((bw + KeyboardOverlayGeometry.ClusterGap) * k), top + (float)((bh - UnitHeight(board.Cluster)) * k), (float)k, pressed); if (board.IncludeMouse) DrawMouse(c, new(left + (float)((bw + cw + KeyboardOverlayGeometry.MouseGap) * k), top, left + (float)((bw + cw + KeyboardOverlayGeometry.MouseGap + mw) * k), top + (float)(bh * k)), pressed); return Cache(sig);
    }
    private byte[] Cache(string s) { _pixels = _bitmap!.Bytes.ToArray(); _signature = s; return _pixels; }
    private static double Span(double u) => u + (u - 1) * KeyboardOverlayGeometry.Gap;
    private static double UnitWidth(IReadOnlyList<KeyboardOverlayGeometry.Row> rows) => rows.Select(r => r.Offset + r.Keys.Sum(k => Span(k.Units)) + KeyboardOverlayGeometry.Gap * (r.Keys.Count - 1)).DefaultIfEmpty(0).Max();
    private static double UnitHeight(IReadOnlyList<KeyboardOverlayGeometry.Row> rows) => rows.Count == 0 ? 0 : rows.Count + KeyboardOverlayGeometry.Gap * (rows.Count - 1);
    private static void DrawRows(SKCanvas c, IReadOnlyList<KeyboardOverlayGeometry.Row> rows, float left, float top, float k, IReadOnlySet<string> down) { for (var row = 0; row < rows.Count; row++) { var x = left + (float)(rows[row].Offset * k); var y = top + row * (1 + (float)KeyboardOverlayGeometry.Gap) * k; foreach (var cap in rows[row].Keys) { var w = (float)(Span(cap.Units) * k); DrawKey(c, new(x, y, x + w, y + k), cap.Label, cap.Code is not null && down.Contains(cap.Code)); x += w + (float)KeyboardOverlayGeometry.Gap * k; } } }
    private static void DrawKey(SKCanvas c, SKRect r, string text, bool down) { using var fill = new SKPaint { Color = SKColor.Parse(down ? "#184ED6" : "#202329"), IsAntialias = true }; using var edge = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, r.Height * .055f), IsAntialias = true }; c.DrawRoundRect(r, r.Height * .13f, r.Height * .13f, fill); c.DrawRoundRect(r, r.Height * .13f, r.Height * .13f, edge); using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true }; using var font = new SKFont(SKTypeface.Default, r.Height * .38f); var metrics = font.Metrics; c.DrawText(text, r.MidX, r.MidY - (metrics.Ascent + metrics.Descent) / 2, SKTextAlign.Center, font, paint); }
    // Mirrors KeyboardOverlayPreview.DrawMouse: side tabs under the shell, outlined buttons, wheel capsule.
    private static void DrawMouse(SKCanvas c, SKRect r, IReadOnlySet<string> down)
    {
        using var edge = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, r.Width * .028f), IsAntialias = true };
        float w = r.Width, h = r.Height, shell = w * .48f;
        float sideW = w * .19f, sideH = h * .145f, sideX = r.Left - w * .11f, sideTop = r.Top + h * .46f, sideR = sideW * .38f;
        Shape(SKRect.Create(sideX, sideTop, sideW, sideH), sideR, sideR, sideR, sideR, down.Contains("MouseForward") || down.Contains("MouseX"));
        Shape(SKRect.Create(sideX, sideTop + sideH + h * .03f, sideW, sideH), sideR, sideR, sideR, sideR, down.Contains("MouseBack") || down.Contains("MouseX"));
        Shape(r, shell, shell, w * .36f, w * .36f, false);
        float inset = w * .08f, split = w * .06f, buttonH = h * .44f, buttonW = (w - inset * 2 - split) / 2, buttonR = shell - inset, inner = w * .06f;
        Shape(SKRect.Create(r.Left + inset, r.Top + inset, buttonW, buttonH), buttonR, inner, inner, inner, down.Contains("MouseLeft"));
        Shape(SKRect.Create(r.Right - inset - buttonW, r.Top + inset, buttonW, buttonH), inner, buttonR, inner, inner, down.Contains("MouseRight"));
        float wheelW = w * .12f, wheelH = buttonH * .52f;
        Shape(SKRect.Create(r.MidX - wheelW / 2, r.Top + inset + buttonH * .18f, wheelW, wheelH), wheelW / 2, wheelW / 2, wheelW / 2, wheelW / 2, down.Contains("MouseMiddle"));
        void Shape(SKRect b, float tl, float tr, float br, float bl, bool hit)
        {
            using var rr = new SKRoundRect(); rr.SetRectRadii(b, [new(tl, tl), new(tr, tr), new(br, br), new(bl, bl)]);
            using var fill = new SKPaint { Color = SKColor.Parse(hit ? "#184ED6" : "#202329"), IsAntialias = true };
            c.DrawRoundRect(rr, fill); c.DrawRoundRect(rr, edge);
        }
    }
    public void Dispose() { _bitmap?.Dispose(); _bitmap = null; _pixels = null; }
}
