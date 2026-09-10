using Avalonia.Threading;
using ClypDat.Capture.Abstractions;
using ClypDat.Core.Settings;
using FFmpeg.AutoGen;

namespace ClypDat.App.Services;

/// <summary>Recorder-owned overlay rasterizer. It writes only a fresh encoder
/// frame, never capture or detector surfaces.</summary>
internal sealed class RecorderOverlayBurnComposer : IDisposable
{
    private KeyboardOverlayFrames? _keyboard;
    private (int Width, int Height, string Layout, int KeyHash) _keyboardShape;
    private IReadOnlySet<string>? _drawn;

    public unsafe OverlayBurnResult Compose(AVFrame* frame, int width, int height, OverlayBurnSnapshot snapshot)
    {
        var settings = snapshot.Settings;
        if (!OverlayRecordingMode.IsBurned(settings.RecordingMode)) return default;
        var result = new OverlayBurnResult();
        if (settings.Camera is not null && snapshot.CameraBgra is { Length: 640 * 360 * 4 } camera)
        {
            var bounds = Bounds(settings.CameraTransform, 16d / 9, width, height);
            Blend(frame, width, height, camera, 640, 360, bounds.X, bounds.Y, bounds.Width, bounds.Height, false);
            result = result with { Camera = true };
        }
        if (!string.Equals(settings.KeyboardLayout, "None", StringComparison.OrdinalIgnoreCase))
        {
            var board = Board(settings);
            var aspect = board is null ? KeyboardOverlayCatalog.Get(settings.KeyboardLayout).AspectRatio : CustomKeyboardBoard.AspectRatio(board);
            var bounds = Bounds(settings.KeyboardTransform, aspect, width, height);
            if (bounds.Width >= 2 && bounds.Height >= 2 && RenderKeyboard(settings, board, bounds.Width, bounds.Height, snapshot.PressedKeys))
            {
                Blend(frame, width, height, _keyboard!.Pixels, bounds.Width, bounds.Height, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
                result = result with { Keyboard = true };
            }
        }
        return result;
    }

    private bool RenderKeyboard(OverlayCaptureSettings settings, CustomKeyboardBoardShape? board, int width, int height, IReadOnlySet<string> pressed)
    {
        var hash = settings.KeyboardKeys is null ? 0 : HashCode.Combine(settings.KeyboardKeys.Count, settings.KeyboardKeys[0].Code);
        var shape = (width, height, settings.KeyboardLayout, hash);
        try
        {
            if (_keyboard is null || _keyboardShape != shape)
            {
                _keyboard?.Dispose();
                _keyboard = Dispatcher.UIThread.InvokeAsync(() => new KeyboardOverlayFrames(settings.KeyboardLayout, board, width, height)).GetAwaiter().GetResult();
                _keyboardShape = shape;
                _drawn = null;
            }
            if (_drawn is null || !_drawn.SetEquals(pressed))
            {
                Dispatcher.UIThread.InvokeAsync(() => { _keyboard.Render(pressed); _keyboard.CopyStraightPixels(); }).GetAwaiter().GetResult();
                _drawn = new HashSet<string>(pressed, StringComparer.OrdinalIgnoreCase);
            }
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("Recorder overlay: keyboard rasterization failed.", error);
            return false;
        }
    }

    private static CustomKeyboardBoardShape? Board(OverlayCaptureSettings settings)
    {
        if (settings.KeyboardKeys is not { Count: > 0 } caps) return null;
        IReadOnlyList<CustomKeyCap> Row(int row) => caps.Where(cap => cap.Row == row).Select(cap => new CustomKeyCap(cap.Code, cap.Label, cap.Units)).ToArray();
        var rows = caps.Where(cap => cap.Row >= 0).Select(cap => cap.Row).Distinct().Order().Select(Row).Where(row => row.Count > 0).ToArray();
        var cluster = caps.Where(cap => cap.Row < 0).Select(cap => cap.Row).Distinct().OrderDescending().Select(Row).Where(row => row.Count > 0).ToArray();
        return new CustomKeyboardBoardShape(rows, cluster, settings.KeyboardShowMouse);
    }

    private static (int X, int Y, int Width, int Height) Bounds(OverlayTransform transform, double aspect, int frameWidth, int frameHeight)
    {
        var width = Math.Clamp((int)Math.Round(transform.Width * frameWidth), 1, frameWidth);
        var height = Math.Clamp((int)Math.Round(width / Math.Max(.01, aspect)), 1, frameHeight);
        return (Math.Clamp((int)Math.Round(transform.X * frameWidth), 0, frameWidth - width), Math.Clamp((int)Math.Round(transform.Y * frameHeight), 0, frameHeight - height), width, height);
    }

    private static unsafe void Blend(AVFrame* target, int targetWidth, int targetHeight, byte[] source, int sourceWidth, int sourceHeight, int x, int y, int width, int height, bool alpha)
    {
        for (var dy = 0; dy < height; dy++) for (var dx = 0; dx < width; dx++)
        {
            var sx = dx * sourceWidth / width; var sy = dy * sourceHeight / height;
            var sourceOffset = (sy * sourceWidth + sx) * 4;
            var a = alpha ? source[sourceOffset + 3] : (byte)255;
            if (a == 0) continue;
            var px = x + dx; var py = y + dy;
            var oldY = target->data[0][py * target->linesize[0] + px];
            var b = source[sourceOffset]; var g = source[sourceOffset + 1]; var r = source[sourceOffset + 2];
            var newY = (byte)Math.Clamp((47 * r + 157 * g + 16 * b + 128) >> 8, 16, 235);
            target->data[0][py * target->linesize[0] + px] = (byte)((newY * a + oldY * (255 - a) + 127) / 255);
            var uv = target->data[1] + (py / 2) * target->linesize[1] + (px / 2) * 2;
            var u = (byte)Math.Clamp(((-26 * r - 87 * g + 112 * b + 32768) >> 8), 16, 240);
            var v = (byte)Math.Clamp(((112 * r - 102 * g - 10 * b + 32768) >> 8), 16, 240);
            uv[0] = (byte)((u * a + uv[0] * (255 - a) + 127) / 255);
            uv[1] = (byte)((v * a + uv[1] * (255 - a) + 127) / 255);
        }
    }

    public void Dispose() { _keyboard?.Dispose(); }
}

internal readonly record struct OverlayBurnResult(bool Camera = false, bool Keyboard = false);
