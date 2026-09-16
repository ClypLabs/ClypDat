using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Shapes = Avalonia.Controls.Shapes;
using ClypDat.App.Services;

namespace ClypDat.App.Controls;

/// <summary>
/// Filled says whether the glyph is a silhouette or a line drawing, and it is
/// not decoration: PathIcon fills whatever it is given, so the crosshair and
/// the plus - both single strokes enclosing no area - came out as a solid disc
/// and as nothing at all. A stroked glyph has to be drawn with a pen.
/// </summary>
public sealed record TimelineMarkerAppearance(string Glyph, string ColorKey, string FallbackColor, bool Filled = true);
public sealed record TimelineMarkerGroup(IReadOnlyList<ClipEventMarker> Markers, double CenterX, bool IsMixed);
public sealed class TimelineMarkerActivatedEventArgs(ClipEventMarker marker) : EventArgs
{
    public ClipEventMarker Marker { get; } = marker;
}

public static class TimelineMarkerPresentation
{
    public static TimelineMarkerAppearance AppearanceFor(string? eventId)
    {
        var id = eventId ?? string.Empty;
        if (Contains(id, "death", "dead", "killed")) return new("M7,7 17,17M17,7 7,17", "Semantic_F05A63", "#F05A63", Filled: false);
        if (Contains(id, "assist")) return new("M12,5v14M5,12h14", "Semantic_59B6FF", "#59B6FF", Filled: false);
        if (Contains(id, "objective", "plant", "defuse", "capture")) return new("M12,2 19,5v6c0,5-3,8-7,11-4-3-7-6-7-11V5z", "Semantic_8BD9AE", "#8BD9AE");
        if (Contains(id, "steal", "thief")) return new("M13,2 4,14h7l-1,8 9-12h-7z", "Semantic_F4B73E", "#F4B73E");
        if (Contains(id, "win", "victory")) return new("M6,3h12v3c0,3-2,5-5,6v3h4v3H7v-3h4v-3C8,11,6,9,6,6z", "Semantic_E5A00D", "#E5A00D");
        if (Contains(id, "precision", "headshot", "hs", "shot")) return new("M12,3a9,9 0 1 0 0,18a9,9 0 1 0 0-18m0,4v10m-5-5h10", "Semantic_CB8CFF", "#CB8CFF", Filled: false);
        if (Contains(id, "teamwipe", "team_wipe", "ace", "crown")) return new("M3,18 5,7l4,4 3-7 3,7 4-4 2,11z", "Semantic_F4B73E", "#F4B73E");
        if (Contains(id, "streak", "killstreak", "multi")) return new("M13,2C8,5 6,9 9,12c-2,0-4,2-4,4 0,3 3,5 7,5s7-2 7-6c0-3-2-5-5-6 1-3 0-5-1-7", "Semantic_F05A63", "#F05A63");
        if (Contains(id, "kill", "frag", "elimination")) return new("M12,3a9,9 0 1 0 0,18a9,9 0 1 0 0-18m0,4v10m-5-5h10", "Semantic_59B6FF", "#59B6FF", Filled: false);
        return new("M12,3 21,12 12,21 3,12Z", "TextMuted", "#A8B5C3");
    }

    private static bool Contains(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<TimelineMarkerGroup> Group(IReadOnlyList<ClipEventMarker> markers, double width, TimeSpan duration, double targetWidth = 26, double minimumGap = 4)
    {
        if (width <= 0 || duration <= TimeSpan.Zero) return [];
        var valid = markers.Select((marker, index) => (marker, index))
            .Where(item => double.IsFinite(item.marker.OffsetSeconds) && item.marker.OffsetSeconds >= 0 && item.marker.OffsetSeconds <= duration.TotalSeconds)
            .OrderBy(item => item.marker.OffsetSeconds).ThenBy(item => item.index).ToArray();
        var groups = new List<TimelineMarkerGroup>();
        var current = new List<(ClipEventMarker marker, int index)>();
        var previousX = double.NaN;
        var spacing = targetWidth + minimumGap;
        foreach (var item in valid)
        {
            var x = item.marker.OffsetSeconds / duration.TotalSeconds * width;
            if (current.Count > 0 && x - previousX > spacing)
            {
                groups.Add(CreateGroup(current, width, duration));
                current.Clear();
            }
            current.Add(item);
            previousX = x;
        }
        if (current.Count > 0) groups.Add(CreateGroup(current, width, duration));
        return groups;
    }

    private static TimelineMarkerGroup CreateGroup(List<(ClipEventMarker marker, int index)> items, double width, TimeSpan duration)
    {
        var ordered = items.OrderBy(x => x.marker.OffsetSeconds).ThenBy(x => x.index).Select(x => x.marker).ToArray();
        var median = ordered[ordered.Length / 2].OffsetSeconds / duration.TotalSeconds * width;
        return new(ordered, Math.Clamp(median, 0, width), ordered.Select(x => AppearanceFor(x.EventId).Glyph).Distinct().Count() > 1);
    }
}

public sealed class TimelineMarkersControl : Canvas
{
    public static readonly StyledProperty<IReadOnlyList<ClipEventMarker>?> MarkersProperty = AvaloniaProperty.Register<TimelineMarkersControl, IReadOnlyList<ClipEventMarker>?>(nameof(Markers));
    public static readonly StyledProperty<TimeSpan> DurationProperty = AvaloniaProperty.Register<TimelineMarkersControl, TimeSpan>(nameof(Duration));
    public static readonly StyledProperty<double> VideoTrackHeightProperty = AvaloniaProperty.Register<TimelineMarkersControl, double>(nameof(VideoTrackHeight));
    public event EventHandler<TimelineMarkerActivatedEventArgs>? MarkerActivated;
    private IReadOnlyList<TimelineMarkerGroup>? _groups;
    private double _groupsWidth;
    private Popup? _flyout;

    public IReadOnlyList<ClipEventMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public TimeSpan Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double VideoTrackHeight { get => GetValue(VideoTrackHeightProperty); set => SetValue(VideoTrackHeightProperty, value); }

    static TimelineMarkersControl() => AffectsMeasure<TimelineMarkersControl>(MarkersProperty, DurationProperty, VideoTrackHeightProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkersProperty || change.Property == DurationProperty || change.Property == VideoTrackHeightProperty) Rebuild();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); Rebuild(); }

    private void Rebuild()
    {
        var duration = Duration;
        if (Bounds.Width <= 0 || VideoTrackHeight <= 0 || duration <= TimeSpan.Zero || Markers is null || Markers.Count == 0)
        {
            Children.Clear(); CloseFlyout(); _groups = null; return;
        }
        var groups = TimelineMarkerPresentation.Group(Markers, Bounds.Width, duration);
        if (_groups is not null && Math.Abs(_groupsWidth - Bounds.Width) < 0.01 && groups.Count == _groups.Count &&
            groups.Zip(_groups).All(pair => pair.First.Markers.SequenceEqual(pair.Second.Markers) && Math.Abs(pair.First.CenterX - pair.Second.CenterX) < 0.01)) return;
        CloseFlyout(); Children.Clear(); _groups = groups;
        _groupsWidth = Bounds.Width;
        foreach (var group in groups) AddGroup(group);
    }

    // A pin, not a dot. The old marker was a 24px disc floating above the lane
    // with a filled glyph inside, which at a glance was a coloured circle and
    // nothing else: the event it stood for was only readable from the tooltip,
    // and it did not look attached to any particular frame. This draws a halo,
    // a ringed disc carrying the glyph in the event's own colour, and a stem
    // down to the lane edge so the eye can follow it to the time it marks.
    private const double MarkerWidth = 26;
    private const double DiscSize = 22;
    private const double StemHeight = 8;
    private const double GlyphSize = 11;
    private const double GlyphStroke = 2;
    private const double MarkerHeight = DiscSize + StemHeight;

    private void AddGroup(TimelineMarkerGroup group)
    {
        var representative = group.Markers[0];
        var appearance = group.IsMixed
            ? new(string.Empty, "TextMuted", "#A8B5C3")
            : TimelineMarkerPresentation.AppearanceFor(representative.EventId);
        var accent = AppThemeService.Brush(appearance.ColorKey, appearance.FallbackColor);

        // A cluster of different events has no glyph that honestly represents
        // it - the old three-line "list" icon said "menu" more than "several
        // things happened here". The count IS the content in that case, and it
        // is the one thing the viewer needs before deciding to open the list.
        Control icon = group.IsMixed
            ? new TextBlock
            {
                Text = group.Markers.Count.ToString(),
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = accent
            }
            : BuildGlyph(appearance, accent);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;

        var content = new Panel { Width = MarkerWidth, Height = MarkerHeight };

        // Drawn before the stem so the stem appears to leave the disc rather
        // than pass behind a translucent halo.
        content.Children.Add(new Border
        {
            Width = MarkerWidth,
            Height = MarkerWidth,
            CornerRadius = new CornerRadius(MarkerWidth / 2),
            Background = Fade(accent, 0.22),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            // Offset the larger halo so its centre matches the disc's centre.
            Margin = new Thickness(0, (DiscSize - MarkerWidth) / 2, 0, 0)
        });
        content.Children.Add(new Shapes.Rectangle
        {
            Width = 2,
            Height = StemHeight + 2,
            RadiusX = 1,
            RadiusY = 1,
            Fill = Fade(accent, 0.75),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom
        });
        content.Children.Add(new Border
        {
            Width = DiscSize,
            Height = DiscSize,
            CornerRadius = new CornerRadius(DiscSize / 2),
            // Nearly opaque, because the lane behind it is video: a translucent
            // disc took whatever colour the frame under it happened to be.
            Background = new SolidColorBrush(Color.FromArgb(242, 10, 16, 21)),
            BorderBrush = accent,
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = icon
        });

        // Same-event clusters keep their glyph and carry the count as a badge.
        // It sits on the disc rather than beside it, with the lane's own dark as
        // a ring, so it reads as part of the pin instead of a second marker
        // crowding the first.
        if (group.Markers.Count > 1 && !group.IsMixed)
            content.Children.Add(new Border
            {
                Background = accent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(10, 16, 21)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                MinWidth = 15,
                Height = 15,
                Padding = new Thickness(3, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -1, 0, 0),
                Child = new TextBlock
                {
                    Text = group.Markers.Count.ToString(),
                    FontSize = 9,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(10, 16, 21)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                }
            });

        var button = new Button
        {
            Content = content,
            Width = MarkerWidth,
            Height = MarkerHeight,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Tag = group,
            RenderTransformOrigin = RelativePoint.Parse("50%,80%")
        };
        button.Classes.Add("timelineMarker");
        var time = ClipDurationFormatter.Format(TimeSpan.FromSeconds(representative.OffsetSeconds));
        AutomationProperties.SetName(button, group.Markers.Count == 1 ? $"{representative.EventLabel}, {time}" : $"{group.Markers.Count} events at {time}");
        ToolTip.SetTip(button, group.Markers.Count == 1 ? $"{representative.EventLabel}  ·  {time}" : $"{group.Markers.Count} events  ·  {time}");
        button.Click += GroupButton_OnClick;
        SetLeft(button, Math.Clamp(group.CenterX - MarkerWidth / 2, 0, Math.Max(0, Bounds.Width - MarkerWidth)));
        SetTop(button, Math.Max(0, VideoTrackHeight - MarkerHeight));
        Children.Add(button);
    }

    // Centred by transforming the geometry, not by asking a layout panel to do
    // it. Avalonia draws a Path at the coordinates its data actually uses, and
    // these glyphs are drawn on a 24x24 grid: the flame occupies roughly x 5-19
    // and the X only 7-17, so each one landed a different distance from the
    // middle of its box. Stretch and Viewbox both scale the box rather than
    // recentre the drawing inside it, which is why the flame sat low and right.
    //
    // Measuring first, then moving the glyph's own centre onto the middle of
    // the icon box, puts every mark in the same place whatever grid it was
    // drawn on. Stroked glyphs measure with their pen, so a thick stroke on one
    // side cannot push the drawing off centre either.
    private static Control BuildGlyph(TimelineMarkerAppearance appearance, IBrush accent)
    {
        var glyph = Geometry.Parse(appearance.Glyph);
        var pen = appearance.Filled ? null : new Pen(accent, GlyphStroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var bounds = pen is null ? glyph.Bounds : glyph.GetRenderBounds(pen);
        var extent = Math.Max(bounds.Width, bounds.Height);
        if (extent > 0) glyph.Transform = new MatrixTransform(CentreGlyph(bounds, GlyphSize));

        var path = appearance.Filled
            ? new Shapes.Path { Data = glyph, Fill = accent }
            : new Shapes.Path
            {
                Data = glyph,
                Stroke = accent,
                // Scaled with the glyph, so the pen is the same weight on screen
                // whatever grid the drawing came off.
                StrokeThickness = GlyphStroke * (extent > 0 ? GlyphSize / extent : 1),
                StrokeJoin = PenLineJoin.Round,
                StrokeLineCap = PenLineCap.Round
            };

        path.Width = GlyphSize;
        path.Height = GlyphSize;
        return path;
    }

    /// <summary>
    /// Moves the middle of <paramref name="bounds"/> onto the middle of a
    /// square of <paramref name="size"/>, scaled to fit it.
    /// </summary>
    internal static Matrix CentreGlyph(Rect bounds, double size)
    {
        var scale = size / Math.Max(bounds.Width, bounds.Height);
        return Matrix.CreateTranslation(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2)) *
               Matrix.CreateScale(scale, scale) *
               Matrix.CreateTranslation(size / 2, size / 2);
    }

    private static IBrush Fade(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, opacity)
            : new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 255, 255, 255));

    private void GroupButton_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: TimelineMarkerGroup group }) return;
        if (group.Markers.Count == 1) { MarkerActivated?.Invoke(this, new(group.Markers[0])); return; }
        CloseFlyout();
        var panel = new StackPanel { Spacing = 2, MaxHeight = 260 };
        var scroll = new ScrollViewer { Content = panel, MaxHeight = 260, Width = 240 };
        foreach (var marker in group.Markers)
        {
            var row = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, Padding = new Thickness(8, 5), Tag = marker, Content = new TextBlock { Text = $"{marker.EventLabel}  ·  {ClipDurationFormatter.Format(TimeSpan.FromSeconds(marker.OffsetSeconds))}", TextTrimming = TextTrimming.CharacterEllipsis } };
            AutomationProperties.SetName(row, $"{marker.EventLabel}, {ClipDurationFormatter.Format(TimeSpan.FromSeconds(marker.OffsetSeconds))}");
            row.Click += FlyoutRow_OnClick;
            panel.Children.Add(row);
        }
        _flyout = new Popup { PlacementTarget = this, Placement = PlacementMode.Top, IsLightDismissEnabled = true, HorizontalOffset = group.CenterX - 120, VerticalOffset = -4, Child = new Border { Background = AppThemeService.Brush("SurfaceBrush", "#1B2731"), BorderBrush = AppThemeService.Brush("EdgeStrongBrush", "#425466"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Child = scroll } };
        _flyout.Opened += (_, _) => { };
        _flyout.IsOpen = true;
    }

    private void FlyoutRow_OnClick(object? sender, RoutedEventArgs e) { e.Handled = true; if (sender is Button { Tag: ClipEventMarker marker }) MarkerActivated?.Invoke(this, new(marker)); CloseFlyout(); }
    private void CloseFlyout() { if (_flyout is not null) { _flyout.IsOpen = false; _flyout.Child = null; _flyout = null; } }
}
