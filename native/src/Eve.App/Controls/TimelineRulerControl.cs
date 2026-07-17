using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Eve.App.Controls;

public sealed class TimelineRulerControl : Control
{
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<TimelineRulerControl, TimeSpan>(nameof(Duration));

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    static TimelineRulerControl()
    {
        AffectsRender<TimelineRulerControl>(DurationProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var bottom = height - 1;
        var line = new Pen(new SolidColorBrush(Color.Parse("#2C3944")), 1);
        context.DrawLine(line, new Point(0, bottom), new Point(width, bottom));

        var seconds = Duration.TotalSeconds;
        if (seconds <= 0) return;

        var minorPen = new Pen(new SolidColorBrush(Color.Parse("#40505D")), 1);
        var majorPen = new Pen(new SolidColorBrush(Color.Parse("#566672")), 1);
        var textBrush = new SolidColorBrush(Color.Parse("#A8CFFF"));

        // Adaptive tick density: the old fixed 5s/10s grid put a labelled
        // major tick every 10 SECONDS regardless of clip length - fine for a
        // 60s clip, but a 44-minute session crammed ~265 overlapping labels
        // into the ruler and rendered as smeared garbage. Pick the smallest
        // "nice" step that keeps labels ~90px apart at the current width, so
        // short clips still get their dense grid and long sessions space out
        // to 1m/5m/10m intervals like any video editor.
        var pixelsPerSecond = width / seconds;
        var desiredStep = 90 / pixelsPerSecond;
        double[] niceSteps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        var majorStep = niceSteps.FirstOrDefault(step => step >= desiredStep, niceSteps[^1]);
        // 5 minors per major, dropped to 1 (none between labels) when they'd
        // be too tight to read as separate marks.
        var minorsPerMajor = majorStep / 5 * pixelsPerSecond >= 6 ? 5 : 1;
        var minorStep = majorStep / minorsPerMajor;

        // Index-based instead of accumulating a double so long clips don't
        // pick up floating-point drift across hundreds of ticks.
        var tickCount = (int)(seconds / minorStep);
        for (var i = 0; i <= tickCount; i++)
        {
            var time = i * minorStep;
            DrawTick(context, time, seconds, width, bottom, i % minorsPerMajor == 0, minorPen, majorPen, textBrush);
        }

        // Real clip durations are almost never an exact multiple of the step
        // (e.g. 60.99s, not 60.00s) - only draw the "true end" tick when
        // there's enough separation from the last regular major tick that
        // their labels can't collide into garbage.
        if (seconds % majorStep > majorStep * 0.4)
        {
            DrawTick(context, seconds, seconds, width, bottom, true, minorPen, majorPen, textBrush);
        }
    }

    private static void DrawTick(
        DrawingContext context,
        double time,
        double duration,
        double width,
        double bottom,
        bool major,
        Pen minorPen,
        Pen majorPen,
        IBrush textBrush)
    {
        var x = Math.Clamp(time / duration * width, 0, width);
        context.DrawLine(major ? majorPen : minorPen, new Point(x, bottom), new Point(x, bottom - (major ? 9 : 5)));
        if (!major) return;

        var label = FormatTime(TimeSpan.FromSeconds(time));
        var formatted = new FormattedText(
            label,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            11,
            textBrush);
        var labelX = Math.Clamp(x - formatted.Width / 2, 0, Math.Max(0, width - formatted.Width));
        context.DrawText(formatted, new Point(labelX, 0));
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString("h\\:mm\\:ss")
            : time.ToString("m\\:ss");
    }
}
