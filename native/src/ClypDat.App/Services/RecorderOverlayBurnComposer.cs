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

internal readonly record struct OverlayBurnResult(bool Camera = false, bool Keyboard = false);
