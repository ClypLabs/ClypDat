using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public sealed class TimedEffectRender : IDisposable
{
    public List<ClipOverlayBurnLayer> Text { get; } = [];
    /// <summary>Blur boxes. Mask is a grayscale PNG of shape coverage (white = blurred).</summary>
    public List<(SpotifyOverlayBounds Bounds, double Sigma, string Enable, string? Mask)> Blur { get; } = [];
    /// <summary>Output frame the bounds are in; blurs sample around their box within it.</summary>
    public int FrameWidth { get; private init; }
    public int FrameHeight { get; private init; }
    public bool IsEmpty => Text.Count == 0 && Blur.Count == 0;
    public void Dispose()
    {
        foreach (var layer in Text) layer.Dispose();
        foreach (var mask in Blur.Select(b => b.Mask).OfType<string>()) AudioCapturePipeline.TryDelete(mask);
    }
    private static string F(double n) => n.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Enable(TimedVideoEffect e) => $"gte(t,{F(e.Start)})*lt(t,{F(e.End)})";

    public static async Task<TimedEffectRender> PrepareAsync(IReadOnlyList<TimedVideoEffect> texts, IReadOnlyList<TimedVideoEffect> blurs,
        double start, double end, double speed, int width, int height, CancellationToken token)
    {
        var result = new TimedEffectRender { FrameWidth = width, FrameHeight = height };
        try
        {
            foreach (var e in TimedEffectState.Rebase(blurs, start, end, speed).Where(e => e.Visible))
            {
                var bounds = TimedEffectState.Pixels(e, width, height);
                var mask = Path.Combine(Path.GetTempPath(), $"clypdat-blur-mask-{Guid.NewGuid():N}.png");
                var path = mask;
                if (Dispatcher.UIThread.CheckAccess()) RasterizeMask(e.Shape, bounds, path);
                else await Dispatcher.UIThread.InvokeAsync(() => RasterizeMask(e.Shape, bounds, path));
                result.Blur.Add((bounds, Math.Max(.1, e.Strength * height / 1080), Enable(e), mask));
            }
            foreach (var e in TimedEffectState.Rebase(texts, start, end, speed).Where(e => e.Visible))
            {
                token.ThrowIfCancellationRequested();
                var bounds = TimedEffectState.Pixels(e, width, height);
                var path = Path.Combine(Path.GetTempPath(), $"clypdat-text-{Guid.NewGuid():N}.png");
                result.Text.Add(new(path, bounds, Enable(e), true));
                if (Dispatcher.UIThread.CheckAccess()) Rasterize(e, bounds, height, path);
                else await Dispatcher.UIThread.InvokeAsync(() => Rasterize(e, bounds, height, path));
            }
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>Pixel-centre shape coverage. FFmpeg's alphamerge turns this luma
    /// into the blurred patch's alpha.</summary>
    internal static unsafe void RasterizeMask(string shape, SpotifyOverlayBounds bounds, string path)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(bounds.Width, bounds.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var frame = bitmap.Lock())
        {
            for (var y = 0; y < bounds.Height; y++)
            {
                var row = (uint*)(frame.Address + y * frame.RowBytes);
                for (var x = 0; x < bounds.Width; x++)
                {
                    var value = (byte)Math.Round(255 * Coverage(shape, x + .5, y + .5, bounds.Width, bounds.Height));
                    row[x] = (uint)(value | value << 8 | value << 16 | 255 << 24);
                }
            }
        }
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    internal static double Coverage(string shape, double x, double y, double width, double height)
    {
        var shortSide = Math.Min(width, height);
        var feather = Math.Min(Math.Max(1, shortSide * .05), shortSide * .25);
        var px = x - width / 2;
        var py = y - height / 2;
        var distance = shortSide / 2 - Math.Max(Math.Abs(px), Math.Abs(py));
        if (shape == "Ellipse")
            distance = (1 - Math.Sqrt((px / (width / 2)) * (px / (width / 2)) + (py / (height / 2)) * (py / (height / 2)))) * shortSide / 2;
        else if (shape == "Rounded")
        {
            var radius = shortSide / 5;
            var qx = Math.Abs(px) - (width / 2 - radius);
            var qy = Math.Abs(py) - (height / 2 - radius);
            distance = radius - (Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0));
        }
        var t = Math.Clamp(distance / feather, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static void Rasterize(TimedVideoEffect e, SpotifyOverlayBounds bounds, int frameHeight, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(bounds.Width, bounds.Height), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
            TimedEffectPainter.DrawText(context, e, new Rect(0, 0, bounds.Width, bounds.Height), frameHeight);
        using var file = File.Create(path);
        bitmap.Save(file);
    }
}
