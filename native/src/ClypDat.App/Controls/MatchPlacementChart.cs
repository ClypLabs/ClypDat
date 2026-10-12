using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Controls;

/// <summary>
/// Players left over match time as a step line, from the start of the replay
/// to the moment the player's match ended, with their own eliminations as
/// purple dots and the open clip's span shaded. Drawn in the radar's style.
/// </summary>
public sealed class MatchPlacementChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<MatchCountStep>?> StepsProperty =
        AvaloniaProperty.Register<MatchPlacementChart, IReadOnlyList<MatchCountStep>?>(nameof(Steps));
    public static readonly StyledProperty<IReadOnlyList<double>?> EliminationsProperty =
        AvaloniaProperty.Register<MatchPlacementChart, IReadOnlyList<double>?>(nameof(Eliminations));
    public static readonly StyledProperty<double> EndSecondsProperty =
        AvaloniaProperty.Register<MatchPlacementChart, double>(nameof(EndSeconds));
    public static readonly StyledProperty<double> ClipStartSecondsProperty =
        AvaloniaProperty.Register<MatchPlacementChart, double>(nameof(ClipStartSeconds), double.NaN);
    public static readonly StyledProperty<double> ClipEndSecondsProperty =
        AvaloniaProperty.Register<MatchPlacementChart, double>(nameof(ClipEndSeconds), double.NaN);

    static MatchPlacementChart() =>
        AffectsRender<MatchPlacementChart>(StepsProperty, EliminationsProperty, EndSecondsProperty, ClipStartSecondsProperty, ClipEndSecondsProperty);

    public IReadOnlyList<MatchCountStep>? Steps { get => GetValue(StepsProperty); set => SetValue(StepsProperty, value); }
    public IReadOnlyList<double>? Eliminations { get => GetValue(EliminationsProperty); set => SetValue(EliminationsProperty, value); }
    public double EndSeconds { get => GetValue(EndSecondsProperty); set => SetValue(EndSecondsProperty, value); }
    public double ClipStartSeconds { get => GetValue(ClipStartSecondsProperty); set => SetValue(ClipStartSecondsProperty, value); }
    public double ClipEndSeconds { get => GetValue(ClipEndSecondsProperty); set => SetValue(ClipEndSecondsProperty, value); }

    private const double AxisWidth = 30, AxisHeight = 16, Top = 6, Right = 6;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Steps is not { Count: > 0 } steps || EndSeconds <= 0) return;
        var plot = new Rect(AxisWidth, Top, Math.Max(10, Bounds.Width - AxisWidth - Right), Math.Max(10, Bounds.Height - Top - AxisHeight));
        var end = EndSeconds;
        var top = Math.Max(1, steps[0].Count);
        var accent = AppThemeService.Brush("AccentBrush", "#5864E8");
        var gridPen = new Pen(AppThemeService.Brush("Surface_263743", "#263743"), 1);
        var labelBrush = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6");
        var purple = AppThemeService.Brush("Semantic_CB8CFF", "#CB8CFF");

        double X(double seconds) => plot.Left + Math.Clamp(seconds / end, 0, 1) * plot.Width;
        double Y(double count) => plot.Bottom - Math.Clamp(count / top, 0, 1) * plot.Height;
        int CountAt(double seconds)
        {
            var count = steps[0].Count;
            foreach (var step in steps)
            {
                if (step.Seconds > seconds) break;
                count = step.Count;
            }
            return count;
        }

        foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
            context.DrawLine(gridPen, new Point(plot.Left, plot.Bottom - fraction * plot.Height), new Point(plot.Right, plot.Bottom - fraction * plot.Height));

        if (double.IsFinite(ClipStartSeconds) && double.IsFinite(ClipEndSeconds) && ClipEndSeconds > ClipStartSeconds && ClipStartSeconds < end)
        {
            var shade = accent is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.16) : accent;
            var left = X(ClipStartSeconds);
            context.DrawRectangle(shade, null, new Rect(left, plot.Top, Math.Max(2, X(ClipEndSeconds) - left), plot.Height));
        }

        var line = new StreamGeometry();
        using (var draw = line.Open())
        {
            var y = Y(steps[0].Count);
            draw.BeginFigure(new Point(plot.Left, y), false);
            foreach (var step in steps.Skip(1))
            {
                if (step.Seconds > end) break;
                draw.LineTo(new Point(X(step.Seconds), y));
                y = Y(step.Count);
                draw.LineTo(new Point(X(step.Seconds), y));
            }
            draw.LineTo(new Point(plot.Right, y));
            draw.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(accent, 2, lineJoin: PenLineJoin.Round), line);

        foreach (var seconds in Eliminations ?? [])
        {
            if (seconds < 0 || seconds > end) continue;
            context.DrawEllipse(purple, null, new Point(X(seconds), Y(CountAt(seconds))), 3.5, 3.5);
        }

        var topLabel = Text(top.ToString(CultureInfo.CurrentCulture), labelBrush);
        context.DrawText(topLabel, new Point(plot.Left - topLabel.Width - 6, plot.Top - topLabel.Height / 2));
        var endCount = CountAt(end);
        var endLabel = Text(endCount.ToString(CultureInfo.CurrentCulture), labelBrush);
        context.DrawText(endLabel, new Point(plot.Left - endLabel.Width - 6, Math.Min(plot.Bottom - endLabel.Height / 2, Y(endCount) - endLabel.Height / 2)));
        var startTime = Text("0:00", labelBrush);
        context.DrawText(startTime, new Point(plot.Left, plot.Bottom + 2));
        var endTime = Text(FortniteStatsPresentation.Clock(end), labelBrush);
        context.DrawText(endTime, new Point(plot.Right - endTime.Width, plot.Bottom + 2));
    }

    private static FormattedText Text(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 10, brush);
}
