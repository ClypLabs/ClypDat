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

        for (var time = 0d; time <= seconds + 0.001; time += 5)
        {
            DrawTick(context, time, seconds, width, bottom, time % 10 == 0, minorPen, majorPen, textBrush);
        }

        // Real clip durations are almost never an exact multiple of 5s (e.g.
        // 60.99s, not 60.00s) - the old > 0.5 threshold still drew this extra
        // "true end" tick right on top of the regular one at the nearest
        // multiple of 5 below it, and their text labels overlapped into
        // garbage like "1:00:00" instead of a clean "1:00". Only draw it when
        // there's enough real separation from that last regular tick to not
        // visually collide.
        if (seconds % 5 > 2.0)
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
