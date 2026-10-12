using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Controls;

/// <summary>
/// A radar for the Stats panel: any number of axes from three up, the first
/// at the top and the rest clockwise (five for CS2 and Fortnite build modes,
/// a four-axis diamond for Zero Build). Values arrive already normalised to
/// 0-1 against fixed ranges (MatchStatsPresentation), so the shape means the
/// same thing from one match to the next.
/// </summary>
public sealed class MatchRadarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<MatchRadarAxis>?> AxesProperty =
        AvaloniaProperty.Register<MatchRadarChart, IReadOnlyList<MatchRadarAxis>?>(nameof(Axes));

    public static readonly StyledProperty<MatchRadarAxis?> SelectedAxisProperty =
        AvaloniaProperty.Register<MatchRadarChart, MatchRadarAxis?>(nameof(SelectedAxis));

    static MatchRadarChart() => AffectsRender<MatchRadarChart>(AxesProperty, SelectedAxisProperty);

    public MatchRadarChart() { Focusable = true; }

    public IReadOnlyList<MatchRadarAxis>? Axes { get => GetValue(AxesProperty); set => SetValue(AxesProperty, value); }
    public MatchRadarAxis? SelectedAxis { get => GetValue(SelectedAxisProperty); set => SetValue(SelectedAxisProperty, value); }
    private static readonly Cursor LabelCursor = new(StandardCursorType.Hand);
    private Rect[] _labelBounds = [];
    private int _hoveredAxis = -1;
    internal IReadOnlyList<Rect> LabelBounds => _labelBounds;

    // Measure clearance from the label box edge, not its centre.
    private const double LabelGap = 10;
    private const double LabelPaddingX = 8;
    private const double LabelPaddingY = 5;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _labelBounds = [];
        if (Axes is not { Count: >= 3 } axes) return;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2 + 4);
        var accent = AppThemeService.Brush("AccentBrush", "#5864E8");
        var gridPen = new Pen(AppThemeService.Brush("Surface_263743", "#263743"), 1);
        var labelBrush = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6");
        var valueBrush = AppThemeService.Brush("Text_D6E2F0", "#D6E2F0");
        var labelBackground = AppThemeService.Brush("Surface_0D151C", "#0D151C");
        var labelBorder = AppThemeService.Brush("Surface_345066", "#345066");
        var selectedBackground = AppThemeService.Brush("AccentSelectedBrush", "#202747");
        var labels = axes.Select(axis =>
        {
            var name = Text(axis.Label, 11, FontWeight.SemiBold, axis == SelectedAxis ? accent : axis.Available ? valueBrush : labelBrush);
            var value = Text(axis.Display, 10, FontWeight.Normal, labelBrush);
            return (Name: name, Value: value, Width: Math.Max(name.Width, value.Width), Height: name.Height + value.Height);
        }).ToArray();
        var buttonWidth = Math.Max(72, labels.Max(label => label.Width) + 2 * LabelPaddingX);
        for (var i = 0; i < axes.Count; i++)
            if (axes[i].Explanation.Length > 0)
                labels[i] = (labels[i].Name, labels[i].Value, buttonWidth, labels[i].Height + 2 * LabelPaddingY);
        Point Direction(int index)
        {
            var angle = -Math.PI / 2 + index * 2 * Math.PI / axes.Count;
            return new Point(Math.Cos(angle), Math.Sin(angle));
        }
        var radius = Math.Min(Bounds.Width / 2, Bounds.Height / 2);
        for (var i = 0; i < axes.Count; i++)
        {
            var direction = Direction(i);
            if (Math.Abs(direction.X) > 0.001)
                radius = Math.Min(radius, (Bounds.Width / 2 - labels[i].Width) / Math.Abs(direction.X) - LabelGap);
            if (Math.Abs(direction.Y) > 0.001)
                radius = Math.Min(radius, ((direction.Y < 0 ? centre.Y : Bounds.Height - centre.Y) - labels[i].Height) / Math.Abs(direction.Y) - LabelGap);
        }
        radius = Math.Max(10, radius);

        Point At(int index, double fraction)
        {
            var angle = -Math.PI / 2 + index * 2 * Math.PI / axes.Count;
            return new Point(centre.X + Math.Cos(angle) * radius * fraction, centre.Y + Math.Sin(angle) * radius * fraction);
        }

        foreach (var ring in new[] { 0.25, 0.5, 0.75, 1.0 })
            context.DrawGeometry(null, gridPen, Polygon(Enumerable.Range(0, axes.Count).Select(i => At(i, ring))));
        // An axis with no data keeps its spoke, dashed, and gets no point:
        // the shape joins the axes either side instead of dipping to a 0.
        var missingPen = new Pen(gridPen.Brush, 1, new DashStyle([3, 3], 0));
        for (var i = 0; i < axes.Count; i++) context.DrawLine(axes[i].Available ? gridPen : missingPen, centre, At(i, 1));

        var points = axes.Select((axis, i) => (axis, i)).Where(item => item.axis.Available && double.IsFinite(item.axis.Value))
            .Select(item => At(item.i, Math.Clamp(item.axis.Value, 0, 1))).ToList();
        var fill = accent is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.32) : accent;
        if (points.Count >= 3) context.DrawGeometry(fill, new Pen(accent, 2, lineJoin: PenLineJoin.Round), Polygon(points));
        else if (points.Count == 2) context.DrawLine(new Pen(accent, 2), points[0], points[1]);
        foreach (var point in points) context.DrawEllipse(accent, null, point, 2.5, 2.5);

        _labelBounds = new Rect[axes.Count];
        for (var i = 0; i < axes.Count; i++)
        {
            var direction = Direction(i);
            var anchor = At(i, 1 + LabelGap / radius);
            var (name, value, width, height) = labels[i];
            var left = anchor.X - (direction.X < -0.001 ? width : direction.X > 0.001 ? 0 : width / 2);
            var top = anchor.Y - (direction.Y < -0.001 ? height : direction.Y > 0.001 ? 0 : height / 2);
            _labelBounds[i] = new Rect(left, top, width, height);
            if (axes[i].Explanation.Length > 0)
            {
                var selected = axes[i] == SelectedAxis;
                context.DrawRectangle(selected ? selectedBackground : labelBackground,
                    new Pen(selected || i == _hoveredAxis ? accent : labelBorder, 1.5), _labelBounds[i], 8, 8);
                top += LabelPaddingY;
            }
            context.DrawText(name, new Point(left + (width - name.Width) / 2, top));
            context.DrawText(value, new Point(left + (width - value.Width) / 2, top + name.Height));
        }
    }

    internal bool SelectAxisAt(Point point)
    {
        if (Axes is not { } axes) return false;
        for (var i = 0; i < Math.Min(axes.Count, _labelBounds.Length); i++)
        {
            if (axes[i].Explanation.Length == 0 || !_labelBounds[i].Contains(point)) continue;
            SetCurrentValue(SelectedAxisProperty, SelectedAxis == axes[i] ? null : axes[i]);
            return true;
        }
        return false;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !SelectAxisAt(e.GetPosition(this))) return;
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var hovered = Axes is { } axes ? Enumerable.Range(0, Math.Min(axes.Count, _labelBounds.Length))
            .FirstOrDefault(i => axes[i].Explanation.Length > 0 && _labelBounds[i].Contains(point), -1) : -1;
        Cursor = hovered >= 0 ? LabelCursor : null;
        if (_hoveredAxis == hovered) return;
        _hoveredAxis = hovered;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Cursor = null;
        _hoveredAxis = -1;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var axes = Axes?.Where(axis => axis.Explanation.Length > 0).ToList();
        if (axes is not { Count: > 0 }) return;
        var current = SelectedAxis is { } selected ? axes.IndexOf(selected) : -1;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Up:
                SetCurrentValue(SelectedAxisProperty, axes[current < 0 ? axes.Count - 1 : (current + axes.Count - 1) % axes.Count]);
                break;
            case Key.Right:
            case Key.Down:
                SetCurrentValue(SelectedAxisProperty, axes[(current + 1) % axes.Count]);
                break;
            case Key.Enter:
            case Key.Space:
                SetCurrentValue(SelectedAxisProperty, current < 0 ? axes[0] : null);
                break;
            case Key.Escape:
                SetCurrentValue(SelectedAxisProperty, null);
                break;
            default: return;
        }
        e.Handled = true;
    }

    private static StreamGeometry Polygon(IEnumerable<Point> points)
    {
        var geometry = new StreamGeometry();
        using var draw = geometry.Open();
        var first = true;
        foreach (var point in points)
        {
            if (first) { draw.BeginFigure(point, true); first = false; }
            else draw.LineTo(point);
        }
        if (!first) draw.EndFigure(true);
        return geometry;
    }

    private static FormattedText Text(string text, double size, FontWeight weight, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);
}
