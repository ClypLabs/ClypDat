using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Controls;

/// <summary>
/// Text and blur drawn live over the playing video, in the same owned overlay
/// window as the Spotify card and camera. Its bounds are the crop output frame,
/// so effect coordinates (fractions of that frame) map straight onto it. It
/// hit-tests only on effects and the selected effect's handles; every other
/// point falls through to the overlay layers beneath it.
/// </summary>
public sealed class TimedEffectLayer : Control, ICustomHitTest
{
    private const double HandleSize = 10;
    private const double SnapPixels = 6;
    private static readonly IBrush Accent = AppThemeService.Brush("Semantic_13C8B5", "#13C8B5");
    private static readonly Pen SelectionPen = new(Accent, 1.5);
    private static readonly Pen HoverPen = new(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1, new DashStyle([4, 3], 0));
    private static readonly Pen GuidePen = new(new SolidColorBrush(Color.FromRgb(255, 64, 160)), 1);
    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.FromArgb(150, 120, 130, 140));
    private readonly TimedEffectFrameSource _frames = new();
    private readonly Dictionary<Guid, BlurSurface> _blurs = [];
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

    private sealed class BlurSurface
    {
        public WriteableBitmap? Bitmap;
        public long Key;
    }

    // The blur is decoded at 270 lines and drawn at display size; smooth
    // upscaling keeps it reading as blur rather than as blocks.
    public TimedEffectLayer() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

    public bool IsGestureActive => _gesture is not null;

    /// <summary>Called from the overlay window's 60Hz tick. Rebuilds the blur
    /// pixels for the frame under the playhead and repaints only when anything
    /// drawn has changed.</summary>
    public void Update(MainWindowViewModel model)
    {
        _model = model;
        var time = model.CurrentTime.TotalSeconds;
        _active.Clear();
        foreach (var e in model.BlurEffects) if (IsActive(e, time)) _active.Add((e, true));
        foreach (var e in model.TextEffects) if (IsActive(e, time)) _active.Add((e, false));
        UpdateBlurs(model, time);
        var hash = new HashCode();
        hash.Add(Bounds.Size);
        hash.Add(model.SelectedTimedEffectId);
        hash.Add(_hover);
        hash.Add(_guides);
        foreach (var (effect, blur) in _active)
        {
            hash.Add(effect);
            if (blur && _blurs.TryGetValue(effect.Id, out var surface)) hash.Add(surface.Key);
        }
        var signature = hash.ToHashCode();
        if (signature == _signature) return;
        _signature = signature;
        InvalidateVisual();
    }

    private bool IsActive(TimedVideoEffect e, double time) =>
        (e.Visible && e.Start <= time && e.End > time) || _gesture?.Id == e.Id;

    /// <summary>Writes the displayed video picture to a PNG of the given width.</summary>
    public Func<string, uint, Task<bool>>? Snapshot { get; set; }
    // Paused, the blur switches from decoded chunk frames to a snapshot of the
    // picture libvlc is showing: the clock behind CurrentTime can sit frames
    // away from it, and on moving footage a frame off reads as the box being
    // zoomed. Two shots per settle, the second in case a seek was still landing.
    private static readonly int[] SnapshotDelaysMs = [150, 700];
    private readonly System.Diagnostics.Stopwatch _settle = new();
    private string? _settleKey;
    private int _shots;
    private bool _snapshotInFlight;
    private string? _snapshotKey;
    private TimedEffectFrameSource.Frame? _snapshotFrame;

    private TimedEffectFrameSource.Frame? SnapshotFrame(MainWindowViewModel model, ClipRenderFilters.CropRect crop)
    {
        var key = $"{model.SelectedVideoPath}|{crop}|{model.CurrentTime.Ticks}";
        if (model.IsPlaying || key != _settleKey)
        {
            _settleKey = key;
            _settle.Restart();
            _shots = 0;
        }
        if (_snapshotKey != key) _snapshotFrame = null;
        if (model.IsPlaying) return null;
        if (!_snapshotInFlight && _shots < SnapshotDelaysMs.Length && _settle.ElapsedMilliseconds >= SnapshotDelaysMs[_shots] && Snapshot is { } take)
        {
            _shots++;
            _ = CaptureAsync(take, key, crop, model.SelectedSourceWidth, model.SelectedSourceHeight);
        }
        return _snapshotFrame;
    }

    private async Task CaptureAsync(Func<string, uint, Task<bool>> take, string key, ClipRenderFilters.CropRect crop, int sourceWidth, int sourceHeight)
    {
        _snapshotInFlight = true;
        var path = Path.Combine(Path.GetTempPath(), $"clypdat-blur-{Guid.NewGuid():N}.png");
        try
        {
            if (!await take(path, TimedEffectFrameSource.SnapshotWidth(sourceWidth, sourceHeight, crop))) return;
            var frame = await Task.Run(() => TimedEffectFrameSource.LoadSnapshot(path) is { } shot
                ? TimedEffectFrameSource.FromSnapshot(shot.Pixels, shot.Width, shot.Height, crop, sourceWidth, sourceHeight)
                : null);
            if (frame is null || key != _settleKey) return;
            _snapshotFrame = frame;
            _snapshotKey = key;
        }
        catch (Exception error) { AppLog.Error("Blur snapshot failed", error); }
        finally
        {
            _snapshotInFlight = false;
            try { File.Delete(path); } catch { }
        }
    }

    private void UpdateBlurs(MainWindowViewModel model, double time)
    {
        var crop = model.ActiveCropRect ?? new ClipRenderFilters.CropRect(0, 0, model.SelectedSourceWidth, model.SelectedSourceHeight);
        var duration = model.Duration.TotalSeconds;
        TimedEffectFrameSource.Frame? frame = null;
        if (_active.Any(item => item.Blur))
            frame = SnapshotFrame(model, crop) ?? _frames.Request(model.SelectedVideoPath, crop, time, duration);
        else if (model.BlurEffects.Where(e => e.Visible && e.Start > time && e.Start - time <= 1).MinBy(e => e.Start) is { } upcoming)
            _frames.Request(model.SelectedVideoPath, crop, upcoming.Start, duration);

        foreach (var stale in _blurs.Keys.Where(id => !_active.Any(item => item.Effect.Id == id)).ToArray())
        {
            _blurs[stale].Bitmap?.Dispose();
            _blurs.Remove(stale);
        }
        if (frame is not { } f) return;
        foreach (var (effect, blur) in _active)
        {
            if (!blur) continue;
            var region = TimedEffectState.Pixels(effect, f.Width, f.Height);
            var sigma = Math.Max(.1, effect.Strength * f.Height / 1080);
            var key = HashCode.Combine(f.Id, region, sigma);
            if (!_blurs.TryGetValue(effect.Id, out var surface)) _blurs[effect.Id] = surface = new BlurSurface();
            if (surface.Key == key && surface.Bitmap is not null) continue;
            var pixels = TimedEffectPainter.BlurRegion(f.Pixels, f.Width, f.Height, new PixelRect(region.X, region.Y, region.Width, region.Height), sigma);
            var size = new PixelSize(Math.Min(region.Width, f.Width - Math.Clamp(region.X, 0, f.Width - 1)), Math.Min(region.Height, f.Height - Math.Clamp(region.Y, 0, f.Height - 1)));
            if (surface.Bitmap is null || surface.Bitmap.PixelSize != size)
            {
                surface.Bitmap?.Dispose();
                surface.Bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using (var target = surface.Bitmap.Lock())
                for (var row = 0; row < size.Height; row++)
                    Marshal.Copy(pixels, row * size.Width * 4, target.Address + row * target.RowBytes, size.Width * 4);
            surface.Key = key;
        }
    }

    private Rect RectOf(TimedVideoEffect e) => new(e.X * Bounds.Width, e.Y * Bounds.Height, e.Width * Bounds.Width, e.Height * Bounds.Height);

    public override void Render(DrawingContext context)
    {
        if (_model is not { } model || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        foreach (var (effect, blur) in _active)
        {
            var rect = RectOf(effect);
            if (blur)
            {
                if (_blurs.TryGetValue(effect.Id, out var surface) && surface.Bitmap is { } bitmap)
                    context.DrawImage(bitmap, new Rect(bitmap.Size), rect);
                else context.DrawRectangle(PlaceholderBrush, null, rect);
            }
            else TimedEffectPainter.DrawText(context, effect, rect, Bounds.Height);
        }
        foreach (var (effect, _) in _active)
        {
            var rect = RectOf(effect);
            if (effect.Id == model.SelectedTimedEffectId)
            {
                context.DrawRectangle(null, SelectionPen, rect);
                foreach (var (_, handle) in Handles(rect, model.IsBlurEffect(effect.Id)))
                    context.DrawRectangle(Brushes.White, SelectionPen, handle);
            }
            else if (effect.Id == _hover) context.DrawRectangle(null, HoverPen, rect);
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
        Update(model);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_gesture is not { } gesture || _model is not { } model)
        {
            var hit = Hit(point);
            Cursor = hit is { } h ? CursorFor(h.Handle) : Cursor.Default;
            if (_hover != hit?.Effect.Id && _model is not null) { _hover = hit?.Effect.Id; Update(_model); }
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
        Update(model);
    }

    // Hit testing stops at the effect's edge, so leaving it arrives as an exit
    // rather than as a move over empty space.
    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_gesture is not null || _hover is null || _model is null) return;
        _hover = null;
        Update(_model);
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
        Update(model);
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

    /// <summary>Hides everything and frees the decoded frames, e.g. when the
    /// editor closes or the clip has no effects left.</summary>
    public void Clear()
    {
        if (_signature == 0 && _gesture is null && _blurs.Count == 0 && _active.Count == 0) { _frames.Reset(); return; }
        EndGesture();
        _active.Clear();
        _hover = null;
        foreach (var surface in _blurs.Values) surface.Bitmap?.Dispose();
        _blurs.Clear();
        _frames.Reset();
        _snapshotFrame = null;
        _snapshotKey = _settleKey = null;
        _signature = 0;
        InvalidateVisual();
    }

    /// <summary>Stops decoding for good; the owning window is closing.</summary>
    public void Dispose()
    {
        Clear();
        _frames.Dispose();
    }
}
