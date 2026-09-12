using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Controls;

public sealed class TimedEffectSurface : Control
{
    private TimedVideoEffect? _drag;
    private Point _start;
    private bool _blur, _resize, _draw;
    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;
    public override void Render(DrawingContext context)
    {
        if (Model is not { } m) return;
        foreach (var e in m.TextEffects.Concat(m.BlurEffects).Where(e => e.Visible && e.Start <= m.CurrentTime.TotalSeconds && e.End > m.CurrentTime.TotalSeconds))
        {
            if (m.SelectedTimedEffectId != e.Id) continue;
            var r = Rect(e);
            context.DrawRectangle(null, new Pen(Brushes.LimeGreen, 2), r);
            context.DrawRectangle(Brushes.LimeGreen, null, new Rect(r.Right - 6, r.Bottom - 6, 12, 12));
        }
    }
    private Rect Rect(TimedVideoEffect e) => new(e.X * Bounds.Width, e.Y * Bounds.Height, e.Width * Bounds.Width, e.Height * Bounds.Height);
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (Model is not { } m || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _start = e.GetPosition(this);
        _draw = m.DrawBlurRectangle;
        _drag = _draw ? m.BlurEffects.FirstOrDefault(x => x.Id == m.SelectedTimedEffectId) :
            m.TextEffects.Concat(m.BlurEffects).Reverse().FirstOrDefault(x => x.Visible && x.Start <= m.CurrentTime.TotalSeconds && x.End > m.CurrentTime.TotalSeconds && Rect(x).Inflate(6).Contains(_start));
        if (_drag is null) return;
        _blur = m.BlurEffects.Contains(_drag);
        _resize = _start.X >= Rect(_drag).Right - 12 && _start.Y >= Rect(_drag).Bottom - 12;
        m.IsPlaying = false;
        m.SelectedTimedEffectId = _drag.Id;
        e.Pointer.Capture(this); e.Handled = true;
        InvalidateVisual();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } item || Model is not { } m) return;
        var p = e.GetPosition(this);
        var dx = (p.X - _start.X) / Bounds.Width;
        var dy = (p.Y - _start.Y) / Bounds.Height;
        TimedVideoEffect changed;
        if (_draw)
        {
            var x = Math.Clamp(Math.Min(_start.X, p.X) / Bounds.Width, 0, .99);
            var y = Math.Clamp(Math.Min(_start.Y, p.Y) / Bounds.Height, 0, .99);
            changed = item with { X = x, Y = y, Width = Math.Clamp(Math.Abs(dx), .01, 1 - x), Height = Math.Clamp(Math.Abs(dy), .01, 1 - y) };
        }
        else if (_resize) changed = item with { Width = Math.Clamp(item.Width + dx, .01, 1 - item.X), Height = Math.Clamp(item.Height + dy, .01, 1 - item.Y) };
        else changed = item with { X = Math.Clamp(item.X + dx, 0, 1 - item.Width), Y = Math.Clamp(item.Y + dy, 0, 1 - item.Height) };
        var list = _blur ? m.BlurEffects : m.TextEffects;
        var index = list.ToList().FindIndex(x => x.Id == item.Id);
        if (index >= 0) list[index] = changed;
        e.Handled = true; InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag is null) return;
        _drag = null; Model!.DrawBlurRectangle = false;
        e.Pointer.Capture(null); e.Handled = true; Model.NotifyTimedEffectsChanged();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (_drag is null) return;
        _drag = null; Model?.NotifyTimedEffectsChanged();
    }
}
