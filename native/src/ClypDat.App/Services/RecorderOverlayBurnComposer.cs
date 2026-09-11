using ClypDat.Capture.Abstractions;
using ClypDat.Core.Settings;
using FFmpeg.AutoGen;
using SkiaSharp;

namespace ClypDat.App.Services;

/// <summary>Recorder-owned overlay rasterizer. Never calls Avalonia: capture pacing owns it.</summary>
internal sealed class RecorderOverlayBurnComposer : IDisposable
{
    private RecorderKeyboardRasterizer? _keyboard;
    public unsafe OverlayBurnResult Compose(AVFrame* frame, int width, int height, OverlayBurnSnapshot snapshot)
    {
        var settings = snapshot.Settings;
        if (!OverlayRecordingMode.IsBurned(settings.RecordingMode)) return default;
        var result = new OverlayBurnResult();
        if (settings.Camera is not null && snapshot.CameraBgra is { Length: 640 * 360 * 4 } camera)
        { var b = Bounds(settings.CameraTransform, 16d / 9, width, height); Blend(frame, camera, 640, 360, b.X, b.Y, b.Width, b.Height, false); result = result with { Camera = true }; }
        if (!string.Equals(settings.KeyboardLayout, "None", StringComparison.OrdinalIgnoreCase))
        {
            var board = Board(settings); var aspect = board is null ? KeyboardOverlayCatalog.Get(settings.KeyboardLayout).AspectRatio : CustomKeyboardBoard.AspectRatio(board); var b = Bounds(settings.KeyboardTransform, aspect, width, height);
            try { _keyboard ??= new(); Blend(frame, _keyboard.Render(settings.KeyboardLayout, board, b.Width, b.Height, snapshot.PressedKeys), b.Width, b.Height, b.X, b.Y, b.Width, b.Height, true); result = result with { Keyboard = true }; }
            catch (Exception error) { AppLog.Error("Recorder overlay: CPU keyboard rasterization failed; video encoding continues and keyboard is marked unavailable.", error); }
        }
        return result;
    }
    private static CustomKeyboardBoardShape? Board(OverlayCaptureSettings s)
    {
        if (s.KeyboardKeys is not { Count: > 0 } caps) return null;
        IReadOnlyList<CustomKeyCap> Row(int r) => caps.Where(c => c.Row == r).Select(c => new CustomKeyCap(c.Code, c.Label, c.Units)).ToArray();
        return new(caps.Where(c => c.Row >= 0).Select(c => c.Row).Distinct().Order().Select(Row).Where(x => x.Count > 0).ToArray(), caps.Where(c => c.Row < 0).Select(c => c.Row).Distinct().OrderDescending().Select(Row).Where(x => x.Count > 0).ToArray(), s.KeyboardShowMouse);
    }
    private static (int X, int Y, int Width, int Height) Bounds(OverlayTransform t, double aspect, int fw, int fh) { var w = Math.Clamp((int)Math.Round(t.Width * fw), 1, fw); var h = Math.Clamp((int)Math.Round(w / Math.Max(.01, aspect)), 1, fh); return (Math.Clamp((int)Math.Round(t.X * fw), 0, fw - w), Math.Clamp((int)Math.Round(t.Y * fh), 0, fh - h), w, h); }
    private static unsafe void Blend(AVFrame* target, byte[] source, int sw, int sh, int x, int y, int w, int h, bool alpha)
    { for (var dy = 0; dy < h; dy++) for (var dx = 0; dx < w; dx++) { var so = ((dy * sh / h) * sw + dx * sw / w) * 4; var a = alpha ? source[so + 3] : (byte)255; if (a == 0) continue; var px = x + dx; var py = y + dy; var oldY = target->data[0][py * target->linesize[0] + px]; var b = source[so]; var g = source[so + 1]; var r = source[so + 2]; var ny = (byte)Math.Clamp((47 * r + 157 * g + 16 * b + 128) >> 8, 16, 235); target->data[0][py * target->linesize[0] + px] = (byte)((ny * a + oldY * (255 - a) + 127) / 255); var uv = target->data[1] + (py / 2) * target->linesize[1] + (px / 2) * 2; var u = (byte)Math.Clamp((-26 * r - 87 * g + 112 * b + 32768) >> 8, 16, 240); var v = (byte)Math.Clamp((112 * r - 102 * g - 10 * b + 32768) >> 8, 16, 240); uv[0] = (byte)((u * a + uv[0] * (255 - a) + 127) / 255); uv[1] = (byte)((v * a + uv[1] * (255 - a) + 127) / 255); } }
    public void Dispose() => _keyboard?.Dispose();
}

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
    private static void DrawKey(SKCanvas c, SKRect r, string text, bool down) { using var fill = new SKPaint { Color = SKColor.Parse(down ? "#184ED6" : "#202329"), IsAntialias = true }; using var edge = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, r.Height * .055f), IsAntialias = true }; c.DrawRoundRect(r, r.Height * .13f, r.Height * .13f, fill); c.DrawRoundRect(r, r.Height * .13f, r.Height * .13f, edge); using var paint = new SKPaint { Color = SKColors.White, TextSize = r.Height * .38f, TextAlign = SKTextAlign.Center, IsAntialias = true }; c.DrawText(text, r.MidX, r.MidY - (paint.FontMetrics.Ascent + paint.FontMetrics.Descent) / 2, paint); }
    private static void DrawMouse(SKCanvas c, SKRect r, IReadOnlySet<string> down) { using var fill = new SKPaint { Color = SKColor.Parse("#202329"), IsAntialias = true }; using var edge = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, r.Width * .028f), IsAntialias = true }; c.DrawRoundRect(r, r.Width * .48f, r.Width * .48f, fill); c.DrawRoundRect(r, r.Width * .48f, r.Width * .48f, edge); Button(new(r.Left + r.Width * .08f, r.Top + r.Width * .08f, r.MidX - r.Width * .03f, r.Top + r.Height * .48f), down.Contains("MouseLeft")); Button(new(r.MidX + r.Width * .03f, r.Top + r.Width * .08f, r.Right - r.Width * .08f, r.Top + r.Height * .48f), down.Contains("MouseRight")); void Button(SKRect b, bool hit) { using var p = new SKPaint { Color = SKColor.Parse(hit ? "#184ED6" : "#202329"), IsAntialias = true }; c.DrawRoundRect(b, r.Width * .08f, r.Width * .08f, p); } }
    public void Dispose() { _bitmap?.Dispose(); _bitmap = null; _pixels = null; }
}
internal readonly record struct OverlayBurnResult(bool Camera = false, bool Keyboard = false);
