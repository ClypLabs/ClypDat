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
    private readonly Dictionary<Guid, BlurView> _blurs = [];
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

    /// <summary>
    /// One blur on screen: a clip at the effect's box holding the box grown by
    /// three sigma, already blurred. The margin is real neighbouring picture, so
    /// after the clip the edges are fully opaque with no halo. The blur is baked
    /// into the pixels rather than left to a GPU effect: a BlurEffect over a
    /// bitmap rewritten every frame came out unblurred during playback.
    /// </summary>
    private sealed class BlurView
    {
        public readonly Border Clip = new() { ClipToBounds = true, IsHitTestVisible = false };
        public readonly FrameImage Image = new() { IsHitTestVisible = false };
        public WriteableBitmap? Bitmap;
        public int Key;
        public int ShapeKey;
        public BlurView()
        {
            RenderOptions.SetBitmapInterpolationMode(Image, BitmapInterpolationMode.HighQuality);
            Clip.Child = new Canvas { Children = { Image } };
        }
    }

    private sealed class FrameImage : Control
    {
        public Bitmap? Bitmap { get; set; }
        public override void Render(DrawingContext context)
        {
            if (Bitmap is { } bitmap) context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(Bounds.Size));
        }
    }

    /// <summary>Holds the blur views. Sits directly below this layer in the
    /// overlay surface, so text and handles draw over the blurs as text burns in
    /// over blur on export.</summary>
    public Canvas BlurHost { get; } = new() { IsHitTestVisible = false, ClipToBounds = true };

    public bool IsGestureActive => _gesture is not null;

    /// <summary>Called from the overlay window's 60Hz tick. Rebuilds the blur
    /// pixels for the frame under the playhead and repaints only when anything
    /// drawn has changed.</summary>
    public void Update(MainWindowViewModel model) => Update(model, model.CurrentTime);

    /// <summary>As <see cref="Update(MainWindowViewModel)"/>, at the overlay
    /// clock's position, so blur and text follow the picture on screen the same
    /// way the camera and Spotify layers do.</summary>
    public void Update(MainWindowViewModel model, TimeSpan position)
    {
        _model = model;
        _position = position;
        var time = position.TotalSeconds;
        _active.Clear();
        foreach (var e in model.BlurEffects) if (IsActive(e, time)) _active.Add((e, true));
        foreach (var e in model.TextEffects) if (IsActive(e, time)) _active.Add((e, false));
        UpdateBlurs(model, time);
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

    private TimeSpan _position;

    private TimedEffectFrameSource.Frame? SnapshotFrame(MainWindowViewModel model, ClipRenderFilters.CropRect crop, double displayWidth)
    {
        // Keyed on the model's playhead, not the overlay clock: paused, it only
        // moves on a real seek, so the settle timer never restarts on clock jitter.
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
            _ = CaptureAsync(take, key, crop, model.SelectedSourceWidth, model.SelectedSourceHeight, displayWidth);
        }
        return _snapshotFrame;
    }

    private async Task CaptureAsync(Func<string, uint, Task<bool>> take, string key, ClipRenderFilters.CropRect crop, int sourceWidth, int sourceHeight, double displayWidth)
    {
        _snapshotInFlight = true;
        var path = Path.Combine(Path.GetTempPath(), $"clypdat-blur-{Guid.NewGuid():N}.png");
        try
        {
            if (!await take(path, TimedEffectFrameSource.SnapshotWidth(displayWidth, sourceWidth, crop))) return;
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

    // Playing, the overlay clock follows libvlc's input time, and the picture
    // libvlc actually shows sits some frames from it - how many depends on the
    // machine, the clip and the display. On moving footage one frame is enough
    // for the blurred box to visibly slide against the video around it. So once
    // a second the picture on screen is sampled and matched against the decoded
    // frames near the clock; the median of the recent offsets shifts which
    // decoded frame is shown.
    private const int CalibrationIntervalMs = 1000;
    private const double MaximumSyncOffset = .4;
    private readonly System.Diagnostics.Stopwatch _calibrationClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<double> _offsets = [];
    private string? _calibrationPath;
    private bool _calibrating;
    private double _syncOffset;

    private void Calibrate(MainWindowViewModel model, ClipRenderFilters.CropRect crop, TimedEffectFrameSource.DecodeArea area, double clock)
    {
        if (_calibrationPath != model.SelectedVideoPath)
        {
            _calibrationPath = model.SelectedVideoPath;
            _offsets.Clear();
            _syncOffset = 0;
        }
        if (_calibrating || _snapshotInFlight || Snapshot is not { } take || _calibrationClock.ElapsedMilliseconds < CalibrationIntervalMs) return;
        _calibrationClock.Restart();
        _ = CalibrateAsync(take, model.SelectedVideoPath, crop, area, clock, model.SelectedSourceWidth, model.SelectedSourceHeight);
    }

    private async Task CalibrateAsync(Func<string, uint, Task<bool>> take, string path, ClipRenderFilters.CropRect crop,
        TimedEffectFrameSource.DecodeArea area, double clock, int sourceWidth, int sourceHeight)
    {
        _calibrating = true;
        var png = Path.Combine(Path.GetTempPath(), $"clypdat-blur-sync-{Guid.NewGuid():N}.png");
        try
        {
            // Small: a 24x16 thumbnail of the blur area is all the match needs.
            if (!await take(png, (uint)Math.Min(640, Math.Max(2, sourceWidth)))) return;
            var offset = await Task.Run(() =>
            {
                if (TimedEffectFrameSource.LoadSnapshot(png) is not { } shot ||
                    TimedEffectFrameSource.FromSnapshot(shot.Pixels, shot.Width, shot.Height, crop, sourceWidth, sourceHeight) is not { } screen) return null;
                var (pixels, width, height, _) = TimedEffectFrameSource.Extract(screen, area.Area);
                // Frames of another decode area (the box moved meanwhile) are a
                // different size and a different picture; skip them.
                var candidates = _frames.Candidates(clock - MaximumSyncOffset, clock + MaximumSyncOffset)
                    .Where(c => c.Pixels.Length == area.Width * area.Height * 4)
                    .Select(c => (c.Time, TimedEffectFrameSource.Signature(c.Pixels, area.Width, area.Height))).ToList();
                return TimedEffectFrameSource.EstimateOffset(TimedEffectFrameSource.Signature(pixels, width, height), candidates, clock);
            });
            if (offset is not { } found || path != _calibrationPath) return;
            _offsets.Add(found);
            if (_offsets.Count > 7) _offsets.RemoveAt(0);
            var previous = _syncOffset;
            _syncOffset = Math.Clamp(_offsets.Order().ElementAt(_offsets.Count / 2), -MaximumSyncOffset, MaximumSyncOffset);
            if (Math.Abs(_syncOffset - previous) >= 1 / TimedEffectFrameSource.Fps)
                AppLog.Info($"Blur sync offset {_syncOffset * 1000:0} ms (sample {found * 1000:0} ms, {_offsets.Count} samples).");
        }
        catch (Exception error) { AppLog.Error("Blur sync calibration failed", error); }
        finally
        {
            _calibrating = false;
            try { File.Delete(png); } catch { }
        }
    }

    private void UpdateBlurs(MainWindowViewModel model, double time)
    {
        var crop = model.ActiveCropRect ?? new ClipRenderFilters.CropRect(0, 0, model.SelectedSourceWidth, model.SelectedSourceHeight);
        var duration = model.Duration.TotalSeconds;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var displayWidth = Bounds.Width * scaling;
        var displayHeight = Bounds.Height * scaling;
        var blurs = _active.Where(item => item.Blur).Select(item => item.Effect).ToList();
        TimedEffectFrameSource.Frame? frame = null;
        if (blurs.Count > 0)
        {
            var upcoming = model.BlurEffects.Where(e => e.Visible && e.Start > time && e.Start - time <= 1);
            frame = SnapshotFrame(model, crop, displayWidth);
            if (frame is null && TimedEffectFrameSource.PlanArea(blurs.Concat(upcoming), crop, displayWidth, displayHeight) is { } area)
            {
                if (model.IsPlaying) Calibrate(model, crop, area, time);
                frame = _frames.Request(model.SelectedVideoPath, area, Math.Max(0, time + _syncOffset), duration);
            }
        }
        else if (model.BlurEffects.Where(e => e.Visible && e.Start > time && e.Start - time <= 1).MinBy(e => e.Start) is { } next &&
                 TimedEffectFrameSource.PlanArea([next], crop, displayWidth, displayHeight) is { } warm)
            _frames.Request(model.SelectedVideoPath, warm, next.Start, duration);

        foreach (var stale in _blurs.Keys.Where(id => !blurs.Any(e => e.Id == id)).ToArray())
        {
            BlurHost.Children.Remove(_blurs[stale].Clip);
            _blurs[stale].Bitmap?.Dispose();
            _blurs.Remove(stale);
        }
        foreach (var effect in blurs)
        {
            if (!_blurs.TryGetValue(effect.Id, out var view))
            {
                _blurs[effect.Id] = view = new BlurView();
                BlurHost.Children.Add(view.Clip);
            }
            var rect = RectOf(effect);
            Canvas.SetLeft(view.Clip, rect.X);
            Canvas.SetTop(view.Clip, rect.Y);
            view.Clip.Width = rect.Width;
            view.Clip.Height = rect.Height;
            var shapeKey = HashCode.Combine(effect.Shape, rect.Size);
            if (view.ShapeKey != shapeKey)
            {
                view.Clip.Clip = TimedEffectPainter.BlurShape(effect.Shape, new Rect(rect.Size));
                view.ShapeKey = shapeKey;
            }
            view.Clip.Background = view.Bitmap is null ? PlaceholderBrush : null;
            if (frame is not { } f) continue;
            var padded = TimedEffectFrameSource.Padded(effect, crop, clamp: false);
            var key = HashCode.Combine(f.Id, padded, effect.Strength, Bounds.Size);
            if (view.Key == key && view.Bitmap is not null) continue;
            var (sharp, sharpWidth, sharpHeight, covered) = TimedEffectFrameSource.Extract(f, padded);
            // Same relation as export: sigma = Strength × frame height / 1080,
            // in the extract's own pixels (frame height = Area.Height of the crop output).
            var sigmaPixels = effect.Strength * (f.Height / Math.Max(1e-9, f.Area.Height)) / 1080;
            var factor = TimedEffectPainter.WorkingFactor(sigmaPixels);
            var (pixels, width, height) = TimedEffectPainter.Downsample(sharp, sharpWidth, sharpHeight, factor);
            TimedEffectPainter.Blur(pixels, width, height, sigmaPixels / factor);
            var size = new PixelSize(width, height);
            if (view.Bitmap is null || view.Bitmap.PixelSize != size)
            {
                view.Bitmap?.Dispose();
                view.Bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using (var target = view.Bitmap.Lock())
                for (var row = 0; row < height; row++)
                    Marshal.Copy(pixels, row * width * 4, target.Address + row * target.RowBytes, width * 4);
            view.Image.Bitmap = view.Bitmap;
            // A partial last block makes the shrunk buffer reach slightly past
            // the extract; stretch it by the same amount so pixels stay in place.
            view.Image.Width = covered.Width * (width * factor / (double)sharpWidth) * Bounds.Width;
            view.Image.Height = covered.Height * (height * factor / (double)sharpHeight) * Bounds.Height;
            Canvas.SetLeft(view.Image, (covered.X - effect.X) * Bounds.Width);
            Canvas.SetTop(view.Image, (covered.Y - effect.Y) * Bounds.Height);
            view.Image.InvalidateVisual();
            view.Clip.Background = null;
            view.Key = key;
        }
    }

    private Rect RectOf(TimedVideoEffect e) => new(e.X * Bounds.Width, e.Y * Bounds.Height, e.Width * Bounds.Width, e.Height * Bounds.Height);

    public override void Render(DrawingContext context)
    {
        if (_model is not { } model || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        foreach (var (effect, blur) in _active)
            if (!blur) TimedEffectPainter.DrawText(context, effect, RectOf(effect), Bounds.Height);
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

    /// <summary>Hides everything and frees the decoded frames, e.g. when the
    /// editor closes or the clip has no effects left.</summary>
    public void Clear()
    {
        if (_signature == 0 && _gesture is null && _blurs.Count == 0 && _active.Count == 0) { _frames.Reset(); return; }
        EndGesture();
        _active.Clear();
        _hover = null;
        foreach (var view in _blurs.Values) view.Bitmap?.Dispose();
        _blurs.Clear();
        BlurHost.Children.Clear();
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
