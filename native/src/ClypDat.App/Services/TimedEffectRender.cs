using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public sealed class TimedEffectRender : IDisposable
{
    public List<ClipOverlayBurnLayer> Text { get; } = [];
    /// <summary>Blur boxes. Mask is a grayscale PNG of the shape (white = blurred),
    /// null for a plain rectangle.</summary>
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
                string? mask = null;
                if (TimedEffectPainter.BlurShape(e.Shape, new Rect(0, 0, 1, 1)) is not null)
                {
                    mask = Path.Combine(Path.GetTempPath(), $"clypdat-blur-mask-{Guid.NewGuid():N}.png");
                    var path = mask;
                    if (Dispatcher.UIThread.CheckAccess()) RasterizeMask(e.Shape, bounds, path);
                    else await Dispatcher.UIThread.InvokeAsync(() => RasterizeMask(e.Shape, bounds, path));
                }
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

    /// <summary>White shape on black at the box's pixel size; FFmpeg's alphamerge
    /// turns its luma into the blurred patch's alpha.</summary>
    internal static void RasterizeMask(string shape, SpotifyOverlayBounds bounds, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(bounds.Width, bounds.Height), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var box = new Rect(0, 0, bounds.Width, bounds.Height);
            context.DrawRectangle(Avalonia.Media.Brushes.Black, null, box);
            if (TimedEffectPainter.BlurShape(shape, box) is { } geometry) context.DrawGeometry(Avalonia.Media.Brushes.White, null, geometry);
            else context.DrawRectangle(Avalonia.Media.Brushes.White, null, box);
        }
        using var file = File.Create(path);
        bitmap.Save(file);
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
