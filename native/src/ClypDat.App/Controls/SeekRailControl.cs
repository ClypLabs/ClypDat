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
/// (see FullscreenProgressBar_OnPointerPressed), so the trimmed-away spans
/// remain seekable. Fullscreen adds a thumb; the hover bar keeps its geometry.
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

    public static readonly StyledProperty<double> RailThicknessProperty =
        AvaloniaProperty.Register<SeekRailControl, double>(nameof(RailThickness));

    public static readonly StyledProperty<double> ThumbDiameterProperty =
        AvaloniaProperty.Register<SeekRailControl, double>(nameof(ThumbDiameter));

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

    // Zero preserves the original full-height, edge-to-edge hover rail.
    public double RailThickness
    {
        get => GetValue(RailThicknessProperty);
        set => SetValue(RailThicknessProperty, value);
    }

    public double ThumbDiameter
    {
        get => GetValue(ThumbDiameterProperty);
        set => SetValue(ThumbDiameterProperty, value);
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
            RailCornerRadiusProperty,
            RailThicknessProperty,
            ThumbDiameterProperty);
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

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var (left, width) = RailBounds(bounds.Width, ThumbDiameter);
        var height = RailThickness > 0 ? Math.Min(RailThickness, bounds.Height) : bounds.Height;
        var rect = new Rect(left, (bounds.Height - height) / 2, width, height);
        if (rect.Width <= 0) return;

        var radius = Math.Min(RailCornerRadius, Math.Min(rect.Width, rect.Height) / 2);
        var played = PlayedWidth(rect.Width);

        var start = rect.X + Math.Clamp(TrimStartPercent, 0, 100) / 100 * rect.Width;
        var end = rect.X + Math.Clamp(TrimEndPercent, 0, 100) / 100 * rect.Width;
        if (end < start) (start, end) = (end, start);

        var hasHead = start > rect.X + 0.5;
        var hasTail = end < rect.Right - 0.5;
        if (!hasHead && !hasTail)
        {
            DrawRail(context, rect, radius, played);
        }
        else
        {
            if (hasHead) DrawSpan(context, rect, radius, played, new Rect(rect.X, rect.Y, start - rect.X, rect.Height), CutOpacity);
            DrawSpan(context, rect, radius, played, new Rect(start, rect.Y, end - start, rect.Height), 1);
            if (hasTail) DrawSpan(context, rect, radius, played, new Rect(end, rect.Y, rect.Right - end, rect.Height), CutOpacity);
        }

        if (ThumbDiameter > 0)
        {
            var center = new Point(rect.X + played, bounds.Height / 2);
            context.DrawEllipse(Brushes.White, new Pen(PlayedBrush ?? Brushes.White, 2), center,
                ThumbDiameter / 2 - 1, ThumbDiameter / 2 - 1);
        }
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
        using (context.PushClip(new Rect(rect.X, rect.Y, played, rect.Height)))
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

    internal static (double Left, double Width) RailBounds(double width, double thumbDiameter)
    {
        var inset = Math.Min(Math.Max(0, thumbDiameter) / 2, Math.Max(0, width) / 2);
        return (inset, Math.Max(0, width - inset * 2));
    }

    internal static TimeSpan PositionForPointer(TimeSpan duration, double x, double width, double thumbDiameter)
    {
        if (duration <= TimeSpan.Zero) return TimeSpan.Zero;
        var (left, railWidth) = RailBounds(width, thumbDiameter);
        if (railWidth <= 0) return TimeSpan.Zero;
        var fraction = Math.Clamp((x - left) / railWidth, 0, 1);
        return TimeSpan.FromMilliseconds(duration.TotalMilliseconds * fraction);
    }
}
