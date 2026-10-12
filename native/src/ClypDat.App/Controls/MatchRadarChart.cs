using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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

    static MatchRadarChart() => AffectsRender<MatchRadarChart>(AxesProperty);

    public IReadOnlyList<MatchRadarAxis>? Axes { get => GetValue(AxesProperty); set => SetValue(AxesProperty, value); }

    // Room around the web for the axis names and values.
    private const double LabelMargin = 34;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Axes is not { Count: >= 3 } axes) return;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2 + 4);
        var radius = Math.Max(10, Math.Min(Bounds.Width / 2 - LabelMargin - 12, Bounds.Height / 2 - LabelMargin + 6));
        var accent = AppThemeService.Brush("AccentBrush", "#5864E8");
        var gridPen = new Pen(AppThemeService.Brush("Surface_263743", "#263743"), 1);
        var labelBrush = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6");
        var valueBrush = AppThemeService.Brush("Text_D6E2F0", "#D6E2F0");

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

        for (var i = 0; i < axes.Count; i++)
        {
            var anchor = At(i, 1 + LabelMargin / radius * 0.55);
            var name = Text(axes[i].Label, 11, FontWeight.SemiBold, axes[i].Available ? valueBrush : labelBrush);
            var value = Text(axes[i].Display, 10, FontWeight.Normal, labelBrush);
            var width = Math.Max(name.Width, value.Width);
            var top = anchor.Y - (name.Height + value.Height) / 2;
            var left = Math.Clamp(anchor.X - width / 2, 0, Math.Max(0, Bounds.Width - width));
            context.DrawText(name, new Point(left + (width - name.Width) / 2, top));
            context.DrawText(value, new Point(left + (width - value.Width) / 2, top + name.Height));
        }
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
