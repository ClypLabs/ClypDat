using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Controls;

/// <summary>
/// Text and blur clips drawn inside the Video track, over its filmstrip, the
/// way titles sit on a video track in an NLE. Overlapping clips stack into rows
/// that share the lane's height rather than growing the timeline. Only the
/// pills hit-test, so scrubbing, trim handles and markers keep working
/// everywhere else in the lane.
/// </summary>
public sealed class TimedEffectTrackLayer : Control, ICustomHitTest
{
    private const double MaximumRowHeight = 18;
    private const double EdgeGrab = 6;
    private const double SnapPixels = 6;
    private static readonly IBrush TextFill = new SolidColorBrush(Color.FromArgb(235, 124, 92, 214));
    private static readonly IBrush BlurFill = new SolidColorBrush(Color.FromArgb(235, 52, 118, 204));
    private static readonly Pen IdlePen = new(new SolidColorBrush(Color.FromArgb(160, 10, 15, 19)), 1);
    private static readonly Pen SelectedPen = new(Brushes.White, 1.5);
    private static readonly Pen GripPen = new(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 2);
    private static readonly Cursor EdgeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor BodyCursor = new(StandardCursorType.Hand);
    private readonly List<(TimedVideoEffect Effect, bool Blur, Rect Rect)> _pills = [];
    private MainWindowViewModel? _model;
    private Drag? _drag;

    private sealed record Drag(Guid Id, int Edge, double StartX, TimedVideoEffect Initial, IPointer Pointer)
    {
        public bool Changed { get; set; }
    }

    public TimedEffectTrackLayer()
    {
        ClipToBounds = true;
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.TimedEffectsChanged -= OnEffectsChanged;
            _model = DataContext as MainWindowViewModel;
            if (_model is not null) _model.TimedEffectsChanged += OnEffectsChanged;
            InvalidateVisual();
        };
    }

    private void OnEffectsChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void LayoutPills()
    {
        _pills.Clear();
        if (_model is not { } model || model.Duration.TotalSeconds <= 0 || Bounds.Width <= 0) return;
        var all = model.TextEffects.Select(e => (Effect: e, Blur: false)).Concat(model.BlurEffects.Select(e => (Effect: e, Blur: true))).ToList();
        if (all.Count == 0) return;
        var (rows, count) = TimedEffectState.PackRows(all.Select(item => item.Effect));
        var rowHeight = Math.Min(MaximumRowHeight, (Bounds.Height - 4) / count);
        var duration = model.Duration.TotalSeconds;
        foreach (var (effect, blur) in all)
        {
            var x = effect.Start / duration * Bounds.Width;
            var width = Math.Max(6, (effect.End - effect.Start) / duration * Bounds.Width);
            _pills.Add((effect, blur, new Rect(x, 2 + rows[effect.Id] * rowHeight, width, Math.Max(3, rowHeight - 1))));
        }
    }

    public override void Render(DrawingContext context)
    {
        LayoutPills();
        if (_model is not { } model) return;
        foreach (var (effect, blur, rect) in _pills)
        {
            var selected = effect.Id == model.SelectedTimedEffectId;
            using (context.PushOpacity(effect.Visible ? 1 : .45))
            {
                context.DrawRectangle(blur ? BlurFill : TextFill, selected ? SelectedPen : IdlePen, rect, 3, 3);
                if (rect.Height >= 10 && rect.Width > 14)
                {
                    var label = blur ? (effect.Shape == "Rectangle" ? "Blur" : $"Blur · {effect.Shape}") : "T  " + effect.Text.Replace('\n', ' ');
                    var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default,
                        Math.Min(11, rect.Height - 3), Brushes.White);
                    using (context.PushClip(rect.Deflate(new Thickness(5, 0))))
                        context.DrawText(text, new Point(rect.X + 5, rect.Y + (rect.Height - text.Height) / 2));
                }
                if (selected && rect.Width > 16)
                {
                    context.DrawLine(GripPen, new Point(rect.X + 2.5, rect.Y + 3), new Point(rect.X + 2.5, rect.Bottom - 3));
                    context.DrawLine(GripPen, new Point(rect.Right - 2.5, rect.Y + 3), new Point(rect.Right - 2.5, rect.Bottom - 3));
                }
            }
        }
    }

    private (TimedVideoEffect Effect, bool Blur, Rect Rect)? Hit(Point point)
    {
        for (var i = _pills.Count - 1; i >= 0; i--)
            if (_pills[i].Rect.Inflate(new Thickness(2, 1)).Contains(point)) return _pills[i];
        return null;
    }

    private static int EdgeOf(Rect rect, Point point)
    {
        var grab = Math.Min(EdgeGrab, rect.Width / 3);
        return point.X - rect.Left <= grab ? -1 : rect.Right - point.X <= grab ? 1 : 0;
    }

    public bool HitTest(Point point) => _drag is not null || Hit(point) is not null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_model is not { } model || Hit(e.GetPosition(this)) is not { } hit) return;
        var properties = e.GetCurrentPoint(this).Properties;
        e.Handled = true;
        model.SelectTimedEffect(hit.Effect.Id);
        if (properties.IsRightButtonPressed) { OpenMenu(model, hit.Effect); return; }
        if (!properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2 && !hit.Blur) model.RequestTimedEffectCaptionFocus();
        var point = e.GetPosition(this);
        _drag = new Drag(hit.Effect.Id, EdgeOf(hit.Rect, point), point.X, hit.Effect, e.Pointer);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_drag is not { } drag || _model is not { } model)
        {
            Cursor = Hit(point) is { } hit ? (EdgeOf(hit.Rect, point) == 0 ? BodyCursor : EdgeCursor) : Cursor.Default;
            return;
        }
        e.Handled = true;
        var dx = point.X - drag.StartX;
        if (!drag.Changed && Math.Abs(dx) < 3) return;
        drag.Changed = true;
        var duration = model.Duration.TotalSeconds;
        var perPixel = duration / Math.Max(1, Bounds.Width);
        var snaps = new List<double> { 0, duration, model.CurrentTime.TotalSeconds };
        if (model.TrimEnd > model.TrimStart) { snaps.Add(model.TrimStart.TotalSeconds); snaps.Add(model.TrimEnd.TotalSeconds); }
        foreach (var other in model.TextEffects.Concat(model.BlurEffects).Where(item => item.Id != drag.Id)) { snaps.Add(other.Start); snaps.Add(other.End); }
        var moved = TimedEffectManipulation.ApplyTime(drag.Initial, drag.Edge, dx * perPixel, duration, snaps, SnapPixels * perPixel);
        try { model.SetTimedEffect(moved, persist: false); }
        catch (Exception error) { AppLog.Error("Timed effect timing rejected", error); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag is null) return;
        e.Handled = true;
        EndDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_drag is not { } drag) return;
        _drag = null;
        drag.Pointer.Capture(null);
        if (!drag.Changed || _model is null) return;
        try { _model.CommitTimedEffects(); }
        catch (Exception error) { AppLog.Error("Timed effect save failed", error); }
    }

    private void OpenMenu(MainWindowViewModel model, TimedVideoEffect effect)
    {
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception error) { AppLog.Error($"Timed effect {header} failed", error); }
            };
            return item;
        }
        var menu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Item(effect.Visible ? "Hide" : "Show", () => model.SetTimedEffect(effect with { Visible = !effect.Visible }, persist: true)),
                Item("Duplicate", () => model.DuplicateTimedEffect(effect.Id)),
                Item("Delete", () => model.RemoveTimedEffect(effect.Id))
            }
        };
        menu.Open(this);
    }
}
