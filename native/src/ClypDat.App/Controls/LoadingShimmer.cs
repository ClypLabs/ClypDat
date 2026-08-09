using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClypDat.App.Controls;

// A transform is updated directly instead of through XAML animation: Avalonia
// has no RenderTransform animator, and moving this overlay must not re-layout
// the loading grid while the user is resizing the window.
internal sealed class LoadingShimmer : Border
{
    private const double SweepDurationSeconds = 0.75;
    private const double PauseDurationSeconds = 1.0;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<LoadingShimmer> ActiveShimmers = [];
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly TranslateTransform _translate = new();

    static LoadingShimmer()
    {
        Timer.Tick += (_, _) => UpdateShimmers();
    }

    public LoadingShimmer()
    {
        RenderTransform = _translate;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActiveShimmers.Add(this);
        if (!Timer.IsEnabled) Timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActiveShimmers.Remove(this);
        if (ActiveShimmers.Count == 0) Timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private static void UpdateShimmers()
    {
        var cycle = SweepDurationSeconds + PauseDurationSeconds;
        var elapsed = Clock.Elapsed.TotalSeconds % cycle;
        foreach (var shimmer in ActiveShimmers)
        {
            if (!shimmer.IsEffectivelyVisible || shimmer.Bounds.Width <= 0) continue;
            var progress = Math.Min(1, elapsed / SweepDurationSeconds);
            shimmer._translate.X = -shimmer.Bounds.Width + ((shimmer.Bounds.Width * 2) * progress);
        }
    }
}
