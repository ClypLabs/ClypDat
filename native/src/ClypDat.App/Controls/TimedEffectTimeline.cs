using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Controls;

public sealed class TimedEffectTimeline : Control
{
    private readonly List<(TimedVideoEffect Effect, bool Blur, Rect Rect)> _bars = [];
    private TimedVideoEffect? _drag;
    private double _start;
    private int _edge;
    private bool _blur;
    private MainWindowViewModel? _model;
    public TimedEffectTimeline()
    {
        Height = 56; ClipToBounds = true;
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.TimedEffectsChanged -= Changed;
            _model = DataContext as MainWindowViewModel;
            if (_model is not null) _model.TimedEffectsChanged += Changed;
            InvalidateVisual();
        };
    }
    private void Changed(object? sender, EventArgs e) => InvalidateVisual();
    public override void Render(DrawingContext context)
    {
        _bars.Clear();
        if (DataContext is not MainWindowViewModel m || m.Duration.TotalSeconds <= 0) return;
        var y = 0d;
        foreach (var blur in new[] { false, true })
        {
            var ends = new List<double>();
            foreach (var e in (blur ? m.BlurEffects : m.TextEffects).OrderBy(e => e.Start))
            {
                var row = ends.FindIndex(end => end <= e.Start);
                if (row < 0) { row = ends.Count; ends.Add(e.End); } else ends[row] = e.End;
                var r = new Rect(e.Start / m.Duration.TotalSeconds * Bounds.Width, y + row * 26, Math.Max(4, (e.End - e.Start) / m.Duration.TotalSeconds * Bounds.Width), 23);
                _bars.Add((e, blur, r));
                context.DrawRectangle(e.Visible ? (blur ? Brushes.DarkSlateBlue : Brushes.DarkGreen) : Brushes.DimGray, new Pen(m.SelectedTimedEffectId == e.Id ? Brushes.White : Brushes.Gray), r, 3, 3);
                using (context.PushClip(r)) context.DrawText(new FormattedText(blur ? "Blur" : e.Text.Replace('\n', ' '), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White), r.TopLeft + new Vector(4, 3));
            }
            y += Math.Max(1, ends.Count) * 26;
        }
        if (Height != y) Height = y;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        var bar = _bars.LastOrDefault(b => b.Rect.Contains(p));
        if (bar.Effect is null || DataContext is not MainWindowViewModel m) return;
        _drag = bar.Effect; _blur = bar.Blur; _start = p.X;
        _edge = p.X - bar.Rect.Left < 7 ? -1 : bar.Rect.Right - p.X < 7 ? 1 : 0;
        m.SelectedTimedEffectId = _drag.Id;
        e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } item || DataContext is not MainWindowViewModel m) return;
        var delta = (e.GetPosition(this).X - _start) / Bounds.Width * m.Duration.TotalSeconds;
        var start = item.Start; var end = item.End;
        if (_edge < 0) start = Math.Clamp(start + delta, 0, end - .01);
        else if (_edge > 0) end = Math.Clamp(end + delta, start + .01, m.Duration.TotalSeconds);
        else { var length = end - start; start = Math.Clamp(start + delta, 0, Math.Max(0, m.Duration.TotalSeconds - length)); end = start + length; }
        var list = _blur ? m.BlurEffects : m.TextEffects;
        var index = list.ToList().FindIndex(x => x.Id == item.Id);
        if (index >= 0) list[index] = item with { Start = start, End = end };
        e.Handled = true; InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag is null) return;
        _drag = null; e.Pointer.Capture(null); e.Handled = true;
        (DataContext as MainWindowViewModel)?.NotifyTimedEffectsChanged();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (_drag is null) return;
        _drag = null; (DataContext as MainWindowViewModel)?.NotifyTimedEffectsChanged();
    }
}
