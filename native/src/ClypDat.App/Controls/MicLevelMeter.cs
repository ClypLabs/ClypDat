using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClypDat.App.Controls;

// Segmented input meter for Settings > Audio > Mic Test. Bars light up to the
// current level, and the noise gate's threshold is drawn as a line across the
// strip so the user can see which side of it their voice actually lands on -
// which is the only reason the gate slider is comprehensible at all.
//
// Drawn rather than composed from Borders: it repaints on every audio packet
// (~100Hz), and a Panel of 64 child controls being invalidated at that rate is
// layout work for something that is nothing but rectangles.
public sealed class MicLevelMeter : Control
{
    public static readonly StyledProperty<double> LevelDbProperty =
        AvaloniaProperty.Register<MicLevelMeter, double>(nameof(LevelDb), -100);

    public static readonly StyledProperty<double> ThresholdDbProperty =
        AvaloniaProperty.Register<MicLevelMeter, double>(nameof(ThresholdDb), -100);

    public static readonly StyledProperty<double> FloorDbProperty =
        AvaloniaProperty.Register<MicLevelMeter, double>(nameof(FloorDb), -100);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<MicLevelMeter, bool>(nameof(IsActive));

    private const int BarCount = 64;
    private const double BarGapRatio = 0.32;

    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#26313C"));
    private static readonly IBrush LitBrush = new SolidColorBrush(Color.Parse("#2FD9A8"));
    private static readonly IBrush LoudBrush = new SolidColorBrush(Color.Parse("#E5A23D"));
    private static readonly IBrush PeakBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IPen ThresholdPen = new Pen(new SolidColorBrush(Color.Parse("#8FA6BD")), 1.5);

    static MicLevelMeter()
    {
        AffectsRender<MicLevelMeter>(LevelDbProperty, ThresholdDbProperty, FloorDbProperty, IsActiveProperty);
    }

    public double LevelDb
    {
        get => GetValue(LevelDbProperty);
        set => SetValue(LevelDbProperty, value);
    }

    public double ThresholdDb
    {
        get => GetValue(ThresholdDbProperty);
        set => SetValue(ThresholdDbProperty, value);
    }

    public double FloorDb
    {
        get => GetValue(FloorDbProperty);
        set => SetValue(FloorDbProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var floor = FloorDb < 0 ? FloorDb : -100;
        var slot = width / BarCount;
        var barWidth = Math.Max(1, slot * (1 - BarGapRatio));

        // Idle shows the empty strip rather than nothing at all, so the control
        // does not appear broken before the test is started.
        var litBars = IsActive ? (int)Math.Round(Fraction(LevelDb, floor) * BarCount) : 0;

        for (var index = 0; index < BarCount; index++)
        {
            var x = index * slot + (slot - barWidth) / 2;
            var brush = IdleBrush;
            if (index < litBars)
            {
                var position = index / (double)BarCount;
                brush = position > 0.92 ? PeakBrush : position > 0.78 ? LoudBrush : LitBrush;
            }

            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, 0, barWidth, height), 1.5));
        }

        // Only meaningful once the gate is actually in the graph; at the floor
        // there is no gate, so drawing a line at x=0 would be a lie.
        if (ThresholdDb > floor)
        {
            var thresholdX = Fraction(ThresholdDb, floor) * width;
            context.DrawLine(ThresholdPen, new Point(thresholdX, -2), new Point(thresholdX, height + 2));
        }
    }

    private static double Fraction(double db, double floor)
    {
        if (!double.IsFinite(db)) return 0;
        return Math.Clamp((db - floor) / (0 - floor), 0, 1);
    }
}
