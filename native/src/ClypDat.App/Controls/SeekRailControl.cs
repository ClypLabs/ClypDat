using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClypDat.App.Controls;

/// <summary>
/// The playback seek rail for the editor hover bar and the fullscreen bar.
///
/// Both used to be plain ProgressBars - built in code in one place, declared in
/// XAML in the other, with different heights and different track colours - and
/// neither knew a trim existed. A ProgressBar cannot dim its own ends, so the
/// trim shading needs a rendered control; making it one control also collapses
/// the two rails back into a single definition that differs only in size.
///
/// The rail is display only. Seeking stays with the hit strip that wraps it
/// (see FullscreenProgressBar_OnPointerPressed), which is why nothing here is
/// hit-testable and why the trimmed-away spans are still seekable.
/// </summary>
public sealed class SeekRailControl : Control
{
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<SeekRailControl, TimeSpan>(nameof(Duration));

    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<SeekRailControl, TimeSpan>(nameof(Position));

    // Named to match TimelineLaneControl's pair so both bind to the same
    // TrimStartPercentValue / TrimEndPercentValue on the view model.
    public static readonly StyledProperty<double> TrimStartPercentProperty =
        AvaloniaProperty.Register<SeekRailControl, double>(nameof(TrimStartPercent));

    public static readonly StyledProperty<double> TrimEndPercentProperty =
        AvaloniaProperty.Register<SeekRailControl, double>(nameof(TrimEndPercent), 100);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SeekRailControl, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<SeekRailControl, IBrush?>(nameof(PlayedBrush));

    public static readonly StyledProperty<double> RailCornerRadiusProperty =
        AvaloniaProperty.Register<SeekRailControl, double>(nameof(RailCornerRadius));

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public TimeSpan Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double TrimStartPercent
    {
        get => GetValue(TrimStartPercentProperty);
        set => SetValue(TrimStartPercentProperty, value);
    }

    public double TrimEndPercent
    {
        get => GetValue(TrimEndPercentProperty);
        set => SetValue(TrimEndPercentProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? PlayedBrush
    {
        get => GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public double RailCornerRadius
    {
        get => GetValue(RailCornerRadiusProperty);
        set => SetValue(RailCornerRadiusProperty, value);
    }

    static SeekRailControl()
    {
        AffectsRender<SeekRailControl>(
            DurationProperty,
            PositionProperty,
            TrimStartPercentProperty,
            TrimEndPercentProperty,
            TrackBrushProperty,
            PlayedBrushProperty,
            RailCornerRadiusProperty);
    }

    // Painted OVER the played fill rather than replacing it, so the accent
    // still reads through the trimmed-away spans - washed out, but recognisably
    // the theme's colour rather than a neutral grey. Same colour family as
    // TimelineLaneControl.ShadeBrush; heavier alpha because a 3-8px rail has
    // far less area to make the difference legible than a full timeline lane.
    // No diagonal hatch here - that tile is 16px and would be noise at this
    // height.
    private static readonly IBrush ShadeBrush = new SolidColorBrush(Color.FromArgb(150, 10, 15, 19));

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var radius = Math.Min(RailCornerRadius, Math.Min(rect.Width, rect.Height) / 2);

        if (TrackBrush is { } track) context.DrawRectangle(track, null, rect, radius, radius);

        var played = PlayedWidth(rect.Width);
        if (played > 0 && PlayedBrush is { } fill)
        {
            // Clipped rather than drawn as its own rounded rect: a short
            // played span would otherwise be a stubby pill floating on the
            // rail instead of the rail's left end filled in.
            using (context.PushClip(new Rect(0, 0, played, rect.Height)))
            {
                context.DrawRectangle(fill, null, rect, radius, radius);
            }
        }

        DrawTrimShade(context, rect, radius);
    }

    private double PlayedWidth(double width)
    {
        var total = Duration.TotalSeconds;
        if (total <= 0) return 0;
        return Math.Clamp(Position.TotalSeconds / total, 0, 1) * width;
    }

    private void DrawTrimShade(DrawingContext context, Rect rect, double radius)
    {
        var start = Math.Clamp(TrimStartPercent, 0, 100) / 100 * rect.Width;
        var end = Math.Clamp(TrimEndPercent, 0, 100) / 100 * rect.Width;
        if (end < start) (start, end) = (end, start);

        var hasHead = start > 0.5;
        var hasTail = end < rect.Width - 0.5;
        if (!hasHead && !hasTail) return;

        if (hasHead) DrawShade(context, rect, new Rect(0, 0, start, rect.Height), radius);
        if (hasTail) DrawShade(context, rect, new Rect(end, 0, rect.Width - end, rect.Height), radius);

        // Keeps the two boundaries findable once both sides are shaded - at 3px
        // tall the change in tint alone is easy to lose against the picture
        // behind the bar.
        if (PlayedBrush is not { } tick) return;
        if (hasHead) context.FillRectangle(tick, new Rect(start - 0.5, 0, 1, rect.Height));
        if (hasTail) context.FillRectangle(tick, new Rect(end - 0.5, 0, 1, rect.Height));
    }

    // Clipped to the span but drawn as the full rounded rail, so the shade
    // follows the rail's rounded ends instead of squaring them off.
    private static void DrawShade(DrawingContext context, Rect rail, Rect span, double radius)
    {
        if (span.Width <= 0) return;
        using (context.PushClip(span))
        {
            context.DrawRectangle(ShadeBrush, null, rail, radius, radius);
        }
    }
}
