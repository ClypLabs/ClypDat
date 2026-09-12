using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public sealed class TimedEffectRender : IDisposable
{
    public List<ClipOverlayBurnLayer> Text { get; } = [];
    public List<(SpotifyOverlayBounds Bounds, double Sigma, string Enable)> Blur { get; } = [];
    public bool IsEmpty => Text.Count == 0 && Blur.Count == 0;
    public void Dispose() { foreach (var layer in Text) layer.Dispose(); }
    private static string F(double n) => n.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Enable(TimedVideoEffect e) => $"gte(t,{F(e.Start)})*lt(t,{F(e.End)})";

    public static async Task<TimedEffectRender> PrepareAsync(IReadOnlyList<TimedVideoEffect> texts, IReadOnlyList<TimedVideoEffect> blurs,
        double start, double end, double speed, int width, int height, CancellationToken token)
    {
        var result = new TimedEffectRender();
        try
        {
            foreach (var e in TimedEffectState.Rebase(blurs, start, end, speed).Where(e => e.Visible))
                result.Blur.Add((TimedEffectState.Pixels(e, width, height), Math.Max(.1, e.Strength * height / 1080), Enable(e)));
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

    private static void Rasterize(TimedVideoEffect e, SpotifyOverlayBounds bounds, int frameHeight, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(bounds.Width, bounds.Height), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
            TimedEffectPainter.DrawText(context, e, new Rect(0, 0, bounds.Width, bounds.Height), frameHeight);
        using var file = File.Create(path);
        bitmap.Save(file);
    }
}
