using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Views;

public sealed partial class MainWindow
{
    private Window? _spotifyWindow;
    private SpotifyCardPreview? _spotifyPreview;
    private Grid? _spotifySurface;
    private Canvas? _spotifyViewport;
    private SpotifyOverlayAdorner? _spotifyAdorner;
    private ServerPerPixelOverlay? _spotifyPerPixel;
    private CapturedOverlayPlayback? _capturedPlayback;
    private SpotifyRenderSpec? _spotifyPreviewSpec;
    private string? _spotifyPreviewPath;
    private bool _spotifyPreviewDirty = true;
    private Rect _spotifyVideoBounds;
    private SpotifyOverlayGesture? _spotifyGesture;
    private IPointer? _spotifyPointer;
    private bool _spotifyGestureChanged;
    private static readonly Cursor SpotifyMoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor SpotifyNorthWestCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor SpotifyNorthEastCursor = new(StandardCursorType.TopRightCorner);
    private sealed record SpotifyOverlayGesture(string ClipPath, PixelPoint PointerStart, SpotifyOverlayTransform Start,
        SpotifyOverlayDragMode Mode, int FrameWidth, int FrameHeight);
    private void InitializeSpotifyPreview()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        timer.Tick += (_, _) => UpdateSpotifyPreview();
        Opened += (_, _) => timer.Start();
        Closed += (_, _) => { timer.Stop(); EndSpotifyGesture(); _spotifyPerPixel?.Dispose(); _spotifyWindow?.Close(); _spotifyPreview?.Dispose(); _capturedPlayback?.Dispose(); };
    }
    private void UpdateSpotifyPreview()
    {
        var model = ViewModel;
        if (model is null || !model.IsEditorVisible || !IsVisible || WindowState == WindowState.Minimized || IsEditorSurfaceCovered || _playback is null)
        { HideSpotifyPreview(); HideCapturedOverlayPreview(); return; }
        if (_spotifyPreviewDirty || _spotifyPreviewPath != model.SelectedVideoPath)
        {
            _spotifyPreviewSpec = model.SpotifyPreviewSpec();
            _spotifyPreviewPath = model.SelectedVideoPath;
            _spotifyPreviewDirty = false;
        }
        if (_spotifyPreviewSpec is not { } original || !model.Settings.SpotifyOverlayEnabled || !model.SpotifyOverlayLayerVisible || model.SelectedSourceWidth <= 0)
        { HideSpotifyPreview(); UpdateCapturedOverlayPreview(model); return; }
        try
        {
            var spec = original with { Position = model.Settings.SpotifyOverlayPosition, Font = SpotifyOverlayCardRenderer.ResolveFont(),
                DynamicBackground = model.Settings.SpotifyOverlayDynamicBackground, Transform = model.SpotifyOverlayTransform };
            var top = EditorVideoView.PointToScreen(default);
            var bottom = EditorVideoView.PointToScreen(new Point(EditorVideoView.Bounds.Width, EditorVideoView.Bounds.Height));
            var fullWidth = Math.Max(1, bottom.X - top.X);
            var fullHeight = Math.Max(1, bottom.Y - top.Y);
            var ratio = Math.Min((double)fullWidth / model.SelectedSourceWidth, (double)fullHeight / model.SelectedSourceHeight);
            var videoWidth = model.SelectedSourceWidth * ratio;
            var videoHeight = model.SelectedSourceHeight * ratio;
            var x = top.X + (fullWidth - videoWidth) / 2;
            var y = top.Y + (fullHeight - videoHeight) / 2;
            // VLC retains ownership of the crop mask. Place inside its opening.
            if (model.ActiveCropRect is { } crop)
            { x += crop.X * ratio; y += crop.Y * ratio; videoWidth = crop.Width * ratio; videoHeight = crop.Height * ratio; }
            var host = model.IsVideoFullscreen ? FullscreenVideoHost : EditorVideoHost;
            var hostTop = host.PointToScreen(default);
            var hostBottom = host.PointToScreen(new Point(host.Bounds.Width, host.Bounds.Height));
            if (videoWidth <= 0 || videoHeight <= 0) { HideSpotifyPreview(); return; }
            var width = Math.Max(1, (int)videoWidth);
            var height = Math.Max(1, (int)videoHeight);
            if (_spotifyGesture is { } active && (active.FrameWidth != width || active.FrameHeight != height)) EndSpotifyGesture();
            _spotifyVideoBounds = new Rect(x, y, width, height);
            // Geometry belongs to the full projected video, including zoom/pan.
            // Only the visible card pixels are clipped to the editor viewport.
            var projection = SpotifyOverlayLayout.Project(
                new((int)Math.Round(x), (int)Math.Round(y), width, height),
                new(hostTop.X, hostTop.Y, hostBottom.X - hostTop.X, hostBottom.Y - hostTop.Y), spec.Position, spec.Transform);
            var bounds = projection.CardBounds;
            var visible = projection.VisibleBounds;
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                if (_spotifyGesture is null) HideSpotifyPreview();
                else if (_spotifyViewport is not null)
                {
                    // Keep pointer capture while dragging beyond the zoomed
                    // viewport, so moving back can recover the same gesture.
                    _spotifyViewport.Opacity = 0;
                    _spotifyPerPixel?.ShowAndRefresh();
                }
                return;
            }
            if (_spotifyWindow is null)
            {
                _spotifyPreview = new() { IsHitTestVisible = false };
                _spotifyAdorner = new() { IsHitTestVisible = false };
                _spotifySurface = new Grid { Background = Brushes.Transparent, Children = { _spotifyPreview, _spotifyAdorner } };
                _spotifyViewport = new Canvas { Background = Brushes.Transparent, ClipToBounds = true, Children = { _spotifySurface } };
                _spotifySurface.PointerPressed += SpotifySurface_OnPointerPressed;
                _spotifySurface.PointerMoved += SpotifySurface_OnPointerMoved;
                _spotifySurface.PointerReleased += SpotifySurface_OnPointerReleased;
                _spotifySurface.PointerCaptureLost += (_, _) => EndSpotifyGesture();
                _spotifyWindow = new Window { WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, CanResize = false,
                    ShowActivated = false, Topmost = false, Background = Brushes.Transparent,
                    TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }, Content = _spotifyViewport };
                _spotifyWindow.Opened += (_, _) =>
                {
                    var handle = NativeHandleOf(_spotifyWindow);
                    var style = (long)GetWindowLongPtr(handle, GwlExStyle);
                    SetWindowLongPtr(handle, GwlExStyle, (IntPtr)((style | WsExNoActivate) & ~WsExTransparent));
                    if (WindowsPlatformProfile.IsServer())
                    {
                        _spotifyPerPixel = new(_spotifyWindow, _spotifyViewport);
                        WindowTransparencyFallback.ApplyInputSurfaceIfNeeded(_spotifyWindow);
                    }
                };
            }
            _spotifyWindow.Position = new PixelPoint(visible.X, visible.Y);
            _spotifyViewport!.Opacity = 1;
            var dpi = _spotifyWindow.RenderScaling;
            _spotifyWindow.Width = visible.Width / dpi;
            _spotifyWindow.Height = visible.Height / dpi;
            _spotifySurface!.Width = bounds.Width / dpi;
            _spotifySurface.Height = bounds.Height / dpi;
            Canvas.SetLeft(_spotifySurface, (bounds.X - visible.X) / dpi);
            Canvas.SetTop(_spotifySurface, (bounds.Y - visible.Y) / dpi);
            var displayCard = SpotifyOverlayLayout.Resolve(width, height, spec.Position, spec.Transform);
            var displayRaster = SpotifyOverlayLayout.ResolveRenderBounds(width, height, spec.Position, spec.Transform);
            _spotifyAdorner!.CardBounds = new Rect((displayCard.X - displayRaster.X) / dpi, (displayCard.Y - displayRaster.Y) / dpi,
                displayCard.Width / dpi, displayCard.Height / dpi);
            _spotifyAdorner.RotationDegrees = spec.Transform is null ? 0 : SpotifyOverlayLayout.Normalize(width, height, spec.Transform).RotationDegrees;
            _spotifyAdorner.InvalidateVisual();
            _spotifyAdorner!.IsVisible = model.IsSpotifyOverlaySelected && model.HasEditableSpotifyOverlay;
            // A four-times zoom must not allocate a monitor-sized offscreen
            // bitmap four times over. This caps preview raster size only.
            var rasterScale = Math.Min(1, 4096.0 / bounds.Width);
            _spotifyPreview!.Update(spec, model.CurrentTime.TotalSeconds,
                Math.Max(1, (int)Math.Round(width * rasterScale)), Math.Max(1, (int)Math.Round(height * rasterScale)));
            if (!_spotifyPreview.HasCard && !_spotifyAdorner.IsVisible) { HideSpotifyPreview(); return; }
            var handle = NativeHandleOf(_spotifyWindow);
            if (!_spotifyWindow.IsVisible || (handle != IntPtr.Zero && !IsWindowVisible(handle))) _spotifyWindow.Show(this);
            // LibVLC can create or reattach its child HWND after the owned card
            // window. Raise without activation so opening a clip does not wait
            // for unrelated hover controls to repair the stacking order.
            handle = NativeHandleOf(_spotifyWindow);
            if (handle != IntPtr.Zero) SetWindowPos(handle, HwndTop, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
            _spotifyPerPixel?.ShowAndRefresh();
            UpdateCapturedOverlayPreview(model);
            // Captured camera is refreshed after Spotify so it receives the
            // current source time, then restore Spotify as top visual layer.
            handle = NativeHandleOf(_spotifyWindow);
            if (handle != IntPtr.Zero) SetWindowPos(handle, HwndTop, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }
        catch (InvalidOperationException) { HideSpotifyPreview(); }
    }

    private void HideSpotifyPreview()
    {
        _spotifyPerPixel?.Hide();
        _spotifyWindow?.Hide();
        // Hide both native surfaces before releasing pointer capture. Capture
        // loss can finish a drag and save its layout synchronously.
        EndSpotifyGesture();
    }

    private void UpdateCapturedOverlayPreview(MainWindowViewModel model)
    {
        var showCamera = model.HasCameraOverlayLayer && model.CameraOverlayLayerVisible && model.CameraOverlayTransform is not null;
        const string peripherals = "QWERTY Compact";
        var showPeripherals = model.HasPeripheralOverlayLayer && model.PeripheralOverlayLayerVisible && model.PeripheralOverlayTransform is not null;
        if (!showCamera && !showPeripherals)
        { HideCapturedOverlayPreview(); return; }
        var host = model.IsVideoFullscreen ? FullscreenVideoHost : EditorVideoHost;
        var fullWidth = Math.Max(1, host.Bounds.Width);
        var fullHeight = Math.Max(1, host.Bounds.Height);
        var ratio = Math.Min((double)fullWidth / model.SelectedSourceWidth, (double)fullHeight / model.SelectedSourceHeight);
        var width = (int)Math.Max(1, Math.Round(model.SelectedSourceWidth * ratio));
        var height = (int)Math.Max(1, Math.Round(model.SelectedSourceHeight * ratio));
        double x = (fullWidth - width) / 2;
        double y = (fullHeight - height) / 2;
        if (model.ActiveCropRect is { } crop) { x += crop.X * ratio; y += crop.Y * ratio; width = (int)(crop.Width * ratio); height = (int)(crop.Height * ratio); }
        if (showCamera)
        {
            var normalized = VideoOverlayLayout.Normalize(model.CameraOverlayTransform!, VideoOverlayLayout.CameraAspectRatio);
            var layerWidth = Math.Max(1, (int)Math.Round(width * normalized.Width));
            var layerHeight = Math.Max(1, (int)Math.Round(layerWidth / VideoOverlayLayout.CameraAspectRatio));
            _capturedCameraBounds = new Rect(x + width * normalized.X, y + height * normalized.Y, layerWidth, layerHeight);
            if (_capturedPlayback is null)
            {
                _capturedPlayback = new CapturedOverlayPlayback();
                _capturedPlayback.FrameReady += image => CapturedOverlayScene.SetCamera(image, _capturedCameraBounds);
            }
            _capturedPlayback.Request(model.Settings.LibraryFolder, model.SelectedOverlayManifestCamera(), model.CurrentTime.TotalSeconds);
        }
        if (showPeripherals)
        {
            var aspect = KeyboardOverlayCatalog.Get(peripherals).AspectRatio;
            var normalized = VideoOverlayLayout.Normalize(model.PeripheralOverlayTransform!, aspect);
            var layerWidth = Math.Max(1, width * normalized.Width);
            CapturedOverlayScene.SetPeripherals(peripherals, new Rect(x + width * normalized.X, y + height * normalized.Y, layerWidth, layerWidth / aspect));
        }
        CapturedOverlayScene.IsVisible = true;
    }

    private Rect _capturedCameraBounds;
    private void HideCapturedOverlayPreview() => CapturedOverlayScene.IsVisible = false;

    private void SpotifySurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_spotifySurface is null || ViewModel is not { HasEditableSpotifyOverlay: true } model ||
            !e.GetCurrentPoint(_spotifySurface).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(_spotifySurface);
        var mode = model.IsSpotifyOverlaySelected && _spotifyAdorner is not null ? _spotifyAdorner.HitTest(point) : SpotifyOverlayDragMode.Move;
        model.IsSpotifyOverlaySelected = true;
        model.OpenEditorSidebar(EditorSidebarSection.Overlays);
        var width = Math.Max(1, (int)_spotifyVideoBounds.Width);
        var height = Math.Max(1, (int)_spotifyVideoBounds.Height);
        var sourceWidth = Math.Max(1, model.ActiveCropRect?.Width ?? model.SelectedSourceWidth);
        var sourceHeight = Math.Max(1, model.ActiveCropRect?.Height ?? model.SelectedSourceHeight);
        var bounds = SpotifyOverlayLayout.Resolve(sourceWidth, sourceHeight, model.Settings.SpotifyOverlayPosition, model.SpotifyOverlayTransform);
        // Preserve stored precision: rebuilding from a rounded preview width
        // would resize the exported overlay every time it is moved.
        var start = model.SpotifyOverlayTransform ?? new SpotifyOverlayTransform(
            (double)bounds.X / sourceWidth, (double)bounds.Y / sourceHeight, (double)bounds.Width / sourceWidth);
        _spotifyGesture = new(model.SelectedVideoPath, _spotifySurface.PointToScreen(point), start, mode, width, height);
        _spotifyGestureChanged = false;
        _spotifyPointer = e.Pointer;
        e.Pointer.Capture(_spotifySurface);
        e.Handled = true;
        UpdateSpotifyPreview();
    }

    private void SpotifySurface_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_spotifySurface is null) return;
        var point = e.GetPosition(_spotifySurface);
        var mode = _spotifyGesture?.Mode ?? (ViewModel?.IsSpotifyOverlaySelected == true
            && _spotifyAdorner is not null ? _spotifyAdorner.HitTest(point) : SpotifyOverlayDragMode.Move);
        _spotifySurface.Cursor = mode switch
        {
            SpotifyOverlayDragMode.TopLeft or SpotifyOverlayDragMode.BottomRight => SpotifyNorthWestCursor,
            SpotifyOverlayDragMode.TopRight or SpotifyOverlayDragMode.BottomLeft => SpotifyNorthEastCursor,
            SpotifyOverlayDragMode.Rotate => new Cursor(StandardCursorType.Hand),
            _ => SpotifyMoveCursor
        };
        if (_spotifyGesture is not { } gesture || ViewModel is not { } model) return;
        if (model.SelectedVideoPath != gesture.ClipPath || !model.HasEditableSpotifyOverlay) { EndSpotifyGesture(); return; }
        var screen = _spotifySurface.PointToScreen(point);
        if (!_spotifyGestureChanged && Math.Abs(screen.X - gesture.PointerStart.X) < 2 && Math.Abs(screen.Y - gesture.PointerStart.Y) < 2) return;
        _spotifyGestureChanged = true;
        var transform = gesture.Mode == SpotifyOverlayDragMode.Rotate
            ? SpotifyOverlayManipulation.Rotate(gesture.Start,
                new Point(gesture.PointerStart.X - _spotifyVideoBounds.X, gesture.PointerStart.Y - _spotifyVideoBounds.Y),
                new Point(screen.X - _spotifyVideoBounds.X, screen.Y - _spotifyVideoBounds.Y), gesture.FrameWidth, gesture.FrameHeight)
            : SpotifyOverlayManipulation.Apply(gesture.Start, gesture.Mode,
                screen.X - gesture.PointerStart.X, screen.Y - gesture.PointerStart.Y, gesture.FrameWidth, gesture.FrameHeight);
        model.SetSpotifyOverlayTransform(transform, persist: false);
        e.Handled = true;
        // The shared 30fps timer updates position and imagery together.
    }

    private void SpotifySurface_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_spotifyGesture is null) return;
        SpotifySurface_OnPointerMoved(sender, e);
        EndSpotifyGesture();
        e.Handled = true;
    }

    private void EndSpotifyGesture()
    {
        var gesture = _spotifyGesture;
        var changed = _spotifyGestureChanged;
        _spotifyGesture = null;
        _spotifyGestureChanged = false;
        var pointer = _spotifyPointer;
        _spotifyPointer = null;
        pointer?.Capture(null);
        if (changed && gesture is not null && ViewModel?.SelectedVideoPath == gesture.ClipPath)
            ViewModel.CommitSpotifyOverlayTransform();
    }

    private void SpotifyOverlayTrack_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: TrackLaneViewModel { IsOverlay: true } } control ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed || ViewModel is null) return;
        ViewModel.IsSpotifyOverlaySelected = true;
        ViewModel.OpenEditorSidebar(EditorSidebarSection.Overlays);
        e.Handled = true;
        UpdateSpotifyPreview();
    }

    private void SpotifyOverlayReset_OnClick(object? sender, RoutedEventArgs e)
    {
        EndSpotifyGesture();
        ViewModel?.ResetSpotifyOverlayTransform();
        UpdateSpotifyPreview();
    }

    private void SpotifyOverlayEnable_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.SpotifyOverlayEnabled = true;
        UpdateSpotifyPreview();
    }

    private void SpotifyOverlayDone_OnClick(object? sender, RoutedEventArgs e)
    {
        EndSpotifyGesture();
        if (ViewModel is not null) ViewModel.IsSpotifyOverlaySelected = false;
        UpdateSpotifyPreview();
    }

    private void CameraOverlayReset_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ResetCameraOverlayTransform();
        UpdateSpotifyPreview();
    }

    private void PeripheralOverlayReset_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ResetPeripheralOverlayTransform();
        UpdateSpotifyPreview();
    }

    private void CapturedOverlayDone_OnClick(object? sender, RoutedEventArgs e)
    {
        EndSpotifyGesture();
        UpdateSpotifyPreview();
    }
}
