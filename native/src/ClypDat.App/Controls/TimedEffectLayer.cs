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
/// Selection handles and gestures for effects composed by the native video output.
/// Bounds follow the crop frame; unhandled points pass to the artwork gestures.
/// </summary>
public sealed class TimedEffectLayer : Control, ICustomHitTest
{
    private const double HandleSize = 10;
    private const double SnapPixels = 6;
    private static readonly IBrush Accent = AppThemeService.Brush("Semantic_13C8B5", "#13C8B5");
    private static readonly Pen SelectionPen = new(Accent, 1.5);
    private static readonly Pen HoverPen = new(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1, new DashStyle([4, 3], 0));
    private static readonly Pen GuidePen = new(new SolidColorBrush(Color.FromRgb(255, 64, 160)), 1);
    private readonly List<(TimedVideoEffect Effect, bool Blur)> _active = [];
    private MainWindowViewModel? _model;
    private Gesture? _gesture;
    private TimedEffectGuides _guides;
    private Guid? _hover;
    private int _signature;

    private sealed record Gesture(Guid Id, bool Blur, TimedEffectHandle Handle, Point Start, TimedVideoEffect Initial, IPointer Pointer)
    {
        public bool Changed { get; set; }
    }

    private TimeSpan _position;

    public bool IsGestureActive => _gesture is not null;

    /// <summary>Updates selection geometry at the editor clock position.</summary>
    public void Update(MainWindowViewModel model) => Update(model, model.CurrentTime);

    public void Update(MainWindowViewModel model, TimeSpan position)
    {
        _model = model;
        _position = position;
        var time = position.TotalSeconds;
        _active.Clear();
        foreach (var e in model.BlurEffects) if (IsActive(e, time)) _active.Add((e, true));
        foreach (var e in model.TextEffects) if (IsActive(e, time)) _active.Add((e, false));
        var hash = new HashCode();
        hash.Add(Bounds.Size);
        hash.Add(model.SelectedTimedEffectId);
        hash.Add(_hover);
        hash.Add(_guides);
        foreach (var (effect, _) in _active) hash.Add(effect);
        var signature = hash.ToHashCode();
        if (signature == _signature) return;
        _signature = signature;
        InvalidateVisual();
    }

    private bool IsActive(TimedVideoEffect e, double time) =>
        (e.Visible && e.Start <= time && e.End > time) || _gesture?.Id == e.Id;

    private Rect RectOf(TimedVideoEffect e) => new(e.X * Bounds.Width, e.Y * Bounds.Height, e.Width * Bounds.Width, e.Height * Bounds.Height);

    public override void Render(DrawingContext context)
    {
        if (_model is not { } model || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        foreach (var (effect, blur) in _active)
        {
            var rect = RectOf(effect);
            // A shaped blur shows its outline, with the box it is fitted to
            // dashed around it so the handles still read as belonging to it.
            var shape = blur ? TimedEffectPainter.BlurShape(effect.Shape, rect) : null;
            if (effect.Id == model.SelectedTimedEffectId)
            {
                if (shape is null) context.DrawRectangle(null, SelectionPen, rect);
                else { context.DrawRectangle(null, HoverPen, rect); context.DrawGeometry(null, SelectionPen, shape); }
                foreach (var (_, handle) in Handles(rect, blur))
                    context.DrawRectangle(Brushes.White, SelectionPen, handle);
            }
            else if (effect.Id == _hover)
            {
                if (shape is null) context.DrawRectangle(null, HoverPen, rect);
                else context.DrawGeometry(null, HoverPen, shape);
            }
        }
        if (_guides.X is { } gx) context.DrawLine(GuidePen, new Point(gx * Bounds.Width, 0), new Point(gx * Bounds.Width, Bounds.Height));
        if (_guides.Y is { } gy) context.DrawLine(GuidePen, new Point(0, gy * Bounds.Height), new Point(Bounds.Width, gy * Bounds.Height));
    }

    private static IEnumerable<(TimedEffectHandle Handle, Rect Rect)> Handles(Rect r, bool blur)
    {
        Rect At(double x, double y) => new(x - HandleSize / 2, y - HandleSize / 2, HandleSize, HandleSize);
        yield return (TimedEffectHandle.NorthWest, At(r.Left, r.Top));
        yield return (TimedEffectHandle.NorthEast, At(r.Right, r.Top));
        yield return (TimedEffectHandle.SouthWest, At(r.Left, r.Bottom));
        yield return (TimedEffectHandle.SouthEast, At(r.Right, r.Bottom));
        yield return (TimedEffectHandle.West, At(r.Left, r.Center.Y));
        yield return (TimedEffectHandle.East, At(r.Right, r.Center.Y));
        if (!blur && r.Height < HandleSize * 4) yield break;
        yield return (TimedEffectHandle.North, At(r.Center.X, r.Top));
        yield return (TimedEffectHandle.South, At(r.Center.X, r.Bottom));
    }

    private (TimedVideoEffect Effect, bool Blur, TimedEffectHandle Handle)? Hit(Point point)
    {
        if (_model is not { } model) return null;
        foreach (var (effect, blur) in _active)
        {
            if (effect.Id != model.SelectedTimedEffectId) continue;
            foreach (var (handle, rect) in Handles(RectOf(effect), blur))
                if (rect.Inflate(3).Contains(point)) return (effect, blur, handle);
        }
        for (var i = _active.Count - 1; i >= 0; i--)
            if (RectOf(_active[i].Effect).Inflate(2).Contains(point)) return (_active[i].Effect, _active[i].Blur, TimedEffectHandle.Move);
        return null;
    }

    public bool HitTest(Point point) => _gesture is not null || Hit(point) is not null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_model is not { } model || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        if (Hit(point) is not { } hit) return;
        model.SelectTimedEffect(hit.Effect.Id);
        if (e.ClickCount >= 2 && !hit.Blur) model.RequestTimedEffectCaptionFocus();
        _gesture = new Gesture(hit.Effect.Id, hit.Blur, hit.Handle, point, hit.Effect, e.Pointer);
        e.Pointer.Capture(this);
        e.Handled = true;
        Update(model, _position);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_gesture is not { } gesture || _model is not { } model)
        {
            var hit = Hit(point);
            Cursor = hit is { } h ? CursorFor(h.Handle) : Cursor.Default;
            if (_hover != hit?.Effect.Id && _model is not null) { _hover = hit?.Effect.Id; Update(_model, _position); }
            return;
        }
        e.Handled = true;
        var delta = point - gesture.Start;
        if (!gesture.Changed && Math.Abs(delta.X) < 2 && Math.Abs(delta.Y) < 2) return;
        gesture.Changed = true;
        var (effect, guides) = TimedEffectManipulation.Apply(gesture.Initial, gesture.Handle, delta.X / Bounds.Width, delta.Y / Bounds.Height,
            !gesture.Blur, SnapPixels / Bounds.Width, SnapPixels / Bounds.Height);
        _guides = guides;
        try { model.SetTimedEffect(effect, persist: false); }
        catch (Exception error) { AppLog.Error("Timed effect drag rejected", error); }
        Update(model, _position);
    }

    // Hit testing stops at the effect's edge, so leaving it arrives as an exit
    // rather than as a move over empty space.
    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_gesture is not null || _hover is null || _model is null) return;
        _hover = null;
        Update(_model, _position);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_gesture is null) return;
        e.Handled = true;
        EndGesture();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => EndGesture();

    public void EndGesture()
    {
        if (_gesture is not { } gesture) return;
        _gesture = null;
        _guides = default;
        gesture.Pointer.Capture(null);
        if (_model is not { } model) return;
        if (gesture.Changed)
        {
            try { model.CommitTimedEffects(); }
            catch (Exception error) { AppLog.Error("Timed effect save failed", error); }
        }
        Update(model, _position);
    }

    private static readonly Cursor NorthWestCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor SouthEastCursor = new(StandardCursorType.BottomRightCorner);
    private static readonly Cursor NorthEastCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor SouthWestCursor = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor VerticalCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor HorizontalCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    private static Cursor CursorFor(TimedEffectHandle handle) => handle switch
    {
        TimedEffectHandle.NorthWest => NorthWestCursor,
        TimedEffectHandle.SouthEast => SouthEastCursor,
        TimedEffectHandle.NorthEast => NorthEastCursor,
        TimedEffectHandle.SouthWest => SouthWestCursor,
        TimedEffectHandle.North or TimedEffectHandle.South => VerticalCursor,
        TimedEffectHandle.East or TimedEffectHandle.West => HorizontalCursor,
        _ => MoveCursor
    };

    /// <summary>Clears selection and gesture state when the editor closes.</summary>
    public void Clear()
    {
        EndGesture();
        _active.Clear();
        _hover = null;
        _signature = 0;
        InvalidateVisual();
    }
    public void Dispose() => Clear();
}
