using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.Controls;

public sealed record TimelineMarkerAppearance(string Glyph, string ColorKey, string FallbackColor);
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
        if (Contains(id, "death", "dead", "killed")) return new("M7,8a5,5 0 0 1 10,0v2h2v5h-3v3H8v-3H5v-5h2zm2,0v2h6V8a3,3 0 0 0-6,0", "Semantic_F05A63", "#F05A63");
        if (Contains(id, "assist")) return new("M12,5v14M5,12h14", "Semantic_59B6FF", "#59B6FF");
        if (Contains(id, "objective", "plant", "defuse", "capture")) return new("M12,2 19,5v6c0,5-3,8-7,11-4-3-7-6-7-11V5z", "Semantic_8BD9AE", "#8BD9AE");
        if (Contains(id, "steal", "thief")) return new("M13,2 4,14h7l-1,8 9-12h-7z", "Semantic_F4B73E", "#F4B73E");
        if (Contains(id, "win", "victory")) return new("M6,3h12v3c0,3-2,5-5,6v3h4v3H7v-3h4v-3C8,11,6,9,6,6z", "Semantic_E5A00D", "#E5A00D");
        if (Contains(id, "precision", "headshot", "hs", "shot")) return new("M12,3a9,9 0 1 0 0,18a9,9 0 1 0 0-18m0,4v10m-5-5h10", "Semantic_CB8CFF", "#CB8CFF");
        if (Contains(id, "teamwipe", "team_wipe", "ace", "crown")) return new("M3,18 5,7l4,4 3-7 3,7 4-4 2,11z", "Semantic_F4B73E", "#F4B73E");
        if (Contains(id, "streak", "killstreak", "multi")) return new("M13,2C8,5 6,9 9,12c-2,0-4,2-4,4 0,3 3,5 7,5s7-2 7-6c0-3-2-5-5-6 1-3 0-5-1-7", "Semantic_F05A63", "#F05A63");
        if (Contains(id, "kill", "frag", "elimination")) return new("M12,3a9,9 0 1 0 0,18a9,9 0 1 0 0-18m0,4v10m-5-5h10", "Semantic_59B6FF", "#59B6FF");
        return new("M12,3 21,12 12,21 3,12Z", "TextMuted", "#A8B5C3");
    }

    private static bool Contains(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<TimelineMarkerGroup> Group(IReadOnlyList<ClipEventMarker> markers, double width, TimeSpan duration, double targetWidth = 24, double minimumGap = 4)
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

    private void AddGroup(TimelineMarkerGroup group)
    {
        var representative = group.Markers[0];
        var appearance = group.IsMixed ? new("M5,7h14M5,12h14M5,17h14", "TextMuted", "#A8B5C3") : TimelineMarkerPresentation.AppearanceFor(representative.EventId);
        var icon = new PathIcon { Data = Geometry.Parse(appearance.Glyph), Width = 16, Height = 16, Foreground = AppThemeService.Brush(appearance.ColorKey, appearance.FallbackColor) };
        var content = new Grid { Width = 24, Height = 24, Children = { new Border { Background = new SolidColorBrush(Color.FromArgb(190, 18, 25, 32)), CornerRadius = new CornerRadius(12), Child = icon } } };
        if (group.Markers.Count > 1) content.Children.Add(new Border { Background = AppThemeService.Brush("AccentBrush", "#13C8B5"), CornerRadius = new CornerRadius(7), Padding = new Thickness(3, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = group.Markers.Count.ToString(), FontSize = 10, Foreground = Brushes.White } });
        var button = new Button { Content = content, Width = 24, Height = 24, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Tag = group };
        button.Classes.Add("timelineMarker");
        var time = ClipDurationFormatter.Format(TimeSpan.FromSeconds(representative.OffsetSeconds));
        AutomationProperties.SetName(button, group.Markers.Count == 1 ? $"{representative.EventLabel}, {time}" : $"{group.Markers.Count} events at {time}");
        ToolTip.SetTip(button, group.Markers.Count == 1 ? $"{representative.EventLabel} — {time}" : $"{group.Markers.Count} events — {time}");
        button.Click += GroupButton_OnClick;
        SetLeft(button, Math.Clamp(group.CenterX - 12, 0, Math.Max(0, Bounds.Width - 24)));
        SetTop(button, Math.Max(0, VideoTrackHeight - 25));
        Children.Add(button);
    }

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
