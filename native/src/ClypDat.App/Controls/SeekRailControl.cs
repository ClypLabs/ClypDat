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

    // The trimmed-away spans are the SAME rail, drawn faint - not the rail
    // with a dark scrim over it. Overlaying a near-black shade turned those
    // spans into a muddy stripe with its own colour, so a rail with a trim on
    // it carried four competing tones and the cut-but-played head ended up
    // more prominent than the kept-but-unplayed middle. Fading instead keeps
    // the accent recognisably the accent, just receded, and leaves exactly one
    // idea on the bar: this part counts, that part does not.
    private const double CutOpacity = 0.3;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var radius = Math.Min(RailCornerRadius, Math.Min(rect.Width, rect.Height) / 2);
        var played = PlayedWidth(rect.Width);

        var start = Math.Clamp(TrimStartPercent, 0, 100) / 100 * rect.Width;
        var end = Math.Clamp(TrimEndPercent, 0, 100) / 100 * rect.Width;
        if (end < start) (start, end) = (end, start);

        var hasHead = start > 0.5;
        var hasTail = end < rect.Width - 0.5;
        if (!hasHead && !hasTail)
        {
            DrawRail(context, rect, radius, played);
            return;
        }

        if (hasHead) DrawSpan(context, rect, radius, played, new Rect(0, 0, start, rect.Height), CutOpacity);
        DrawSpan(context, rect, radius, played, new Rect(start, 0, end - start, rect.Height), 1);
        if (hasTail) DrawSpan(context, rect, radius, played, new Rect(end, 0, rect.Width - end, rect.Height), CutOpacity);
    }

    private void DrawSpan(DrawingContext context, Rect rect, double radius, double played, Rect span, double opacity)
    {
        if (span.Width <= 0) return;
        using (context.PushClip(span))
        using (context.PushOpacity(opacity))
        {
            DrawRail(context, rect, radius, played);
        }
    }

    // Always the whole rail, clipped by the caller - so the rounded ends stay
    // rounded and a span boundary is a clean vertical cut rather than a pill
    // cap floating mid-bar.
    private void DrawRail(DrawingContext context, Rect rect, double radius, double played)
    {
        if (TrackBrush is { } track) context.DrawRectangle(track, null, rect, radius, radius);
        if (played <= 0 || PlayedBrush is not { } fill) return;
        using (context.PushClip(new Rect(0, 0, played, rect.Height)))
        {
            context.DrawRectangle(fill, null, rect, radius, radius);
        }
    }

    private double PlayedWidth(double width)
    {
        var total = Duration.TotalSeconds;
        if (total <= 0) return 0;
        return Math.Clamp(Position.TotalSeconds / total, 0, 1) * width;
    }
}
