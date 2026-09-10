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
    // One owned native scene sits above LibVLC. A native video child always
    // covers Avalonia siblings, so every editable overlay belongs here.
    private OverlaySceneControl? _capturedOverlayScene;
    private Canvas? _spotifySurface;
    private Canvas? _spotifyViewport;
    private SpotifyOverlayAdorner? _spotifyAdorner;
    private ServerPerPixelOverlay? _spotifyPerPixel;
    private CapturedOverlayPlayback? _capturedPlayback;
    private SpotifyRenderSpec? _spotifyPreviewSpec;
    private string? _spotifyPreviewPath;
    private bool _spotifyPreviewDirty = true;
    // The overlay window only needs to claim the top of the z-band when
    // something below it could have jumped above: its first show, LibVLC
    // reattaching its child HWND on a clip change, or the video rectangle
    // moving. Re-asserting it on every tick is what buried the hover bar - and
    // re-raising the bar on every tick in response is what the paused-badge
    // path already documents as making it flicker.
    private bool _spotifyRaiseNeeded = true;
    private Rect _spotifyRaisedBounds;
    private Rect _spotifyVideoBounds;
    private SpotifyOverlayGesture? _spotifyGesture;
    private CapturedOverlayGesture? _capturedGesture;
    private SpotifyOverlayAdorner? _capturedAdorner;
    private Rect _capturedPeripheralBounds;
    // Loaded once per clip: the recorded input is a whole-clip document and
    // re-reading it on every tick would be absurd.
    private InputCaptureIndex? _capturedInput;
    private string? _capturedInputPath;
    private IPointer? _spotifyPointer;
    private bool _spotifyGestureChanged;
    private static readonly Cursor SpotifyMoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor SpotifyNorthWestCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor SpotifyNorthEastCursor = new(StandardCursorType.TopRightCorner);
    private sealed record SpotifyOverlayGesture(string ClipPath, PixelPoint PointerStart, SpotifyOverlayTransform Start,
        SpotifyOverlayDragMode Mode, int FrameWidth, int FrameHeight);
    // "Camera" or "Peripherals", matching the view model's layer names.
    private sealed record CapturedOverlayGesture(string ClipPath, string Layer, PixelPoint PointerStart,
        VideoOverlayTransform Start, VideoOverlayManipulationMode Mode, double FrameWidth, double FrameHeight);
    private void InitializeSpotifyPreview()
    {
        // 60Hz, matching the playback timer. A 30Hz sampler against a 30fps
        // decode has no margin: an irregular tick lands two frames apart, then
        // zero, which reads as judder.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60) };
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
            _spotifyRaiseNeeded = true;
        }
        if (model.SelectedSourceWidth <= 0) { HideSpotifyPreview(); HideCapturedOverlayPreview(); return; }
        try
        {
            var original = _spotifyPreviewSpec;
            var showSpotify = original is not null && model.Settings.SpotifyOverlayEnabled && model.SpotifyOverlayLayerVisible;
            var showCamera = model.HasCameraOverlayLayer && model.CameraOverlayLayerVisible && model.CameraOverlayTransform is not null;
            var showPeripherals = model.HasPeripheralOverlayLayer && model.PeripheralOverlayLayerVisible && model.PeripheralOverlayTransform is not null;
            if (!showSpotify && !showCamera && !showPeripherals) { HideSpotifyPreview(); HideCapturedOverlayPreview(); return; }
            var spec = original is null ? null : original with { Position = model.Settings.SpotifyOverlayPosition, Font = SpotifyOverlayCardRenderer.ResolveFont(),
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
            if (_spotifyVideoBounds != _spotifyRaisedBounds) { _spotifyRaiseNeeded = true; _spotifyRaisedBounds = _spotifyVideoBounds; }
            // Geometry belongs to the full projected video, including zoom/pan.
            // Only the visible card pixels are clipped to the editor viewport.
            var frameX = (int)Math.Round(x);
            var frameY = (int)Math.Round(y);
            var viewport = new SpotifyOverlayBounds(hostTop.X, hostTop.Y, hostBottom.X - hostTop.X, hostBottom.Y - hostTop.Y);
            // Clip full video scene, not Spotify card-shaped bounds. This is
            // what allows camera/keyboard to exist when Spotify is hidden.
            var visibleLeft = Math.Max(frameX, viewport.X);
            var visibleTop = Math.Max(frameY, viewport.Y);
            var visibleRight = Math.Min(frameX + width, viewport.X + Math.Max(0, viewport.Width));
            var visibleBottom = Math.Min(frameY + height, viewport.Y + Math.Max(0, viewport.Height));
            var visible = new SpotifyOverlayBounds(visibleLeft, visibleTop,
                Math.Max(0, visibleRight - visibleLeft), Math.Max(0, visibleBottom - visibleTop));
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
                _capturedOverlayScene = new() { IsHitTestVisible = false };
                _spotifyPreview = new() { IsHitTestVisible = false };
                _spotifyAdorner = new() { IsHitTestVisible = false };
                _capturedAdorner = new() { IsHitTestVisible = false, ShowRotationHandle = false };
                _spotifySurface = new Canvas { Background = Brushes.Transparent, Children = { _capturedOverlayScene, _capturedAdorner, _spotifyPreview, _spotifyAdorner } };
                _spotifyViewport = new Canvas { Background = Brushes.Transparent, ClipToBounds = true, Children = { _spotifySurface } };
                _spotifySurface.PointerPressed += SpotifySurface_OnPointerPressed;
                _spotifySurface.PointerMoved += SpotifySurface_OnPointerMoved;
                _spotifySurface.PointerReleased += SpotifySurface_OnPointerReleased;
                _spotifySurface.PointerCaptureLost += (_, _) => { EndCapturedOverlayGesture(); EndSpotifyGesture(); };
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
            _spotifySurface!.Width = width / dpi;
            _spotifySurface.Height = height / dpi;
            Canvas.SetLeft(_spotifySurface, (x - visible.X) / dpi);
            Canvas.SetTop(_spotifySurface, (y - visible.Y) / dpi);
            // Any editable layer keeps the surface live. Gating on Spotify alone
            // meant a clip with only a camera overlay received no pointer events
            // at all, so its overlay could never be moved.
            _spotifySurface.IsHitTestVisible = showSpotify || model.HasCameraOverlayLayer || model.HasPeripheralOverlayLayer;
            UpdateCapturedOverlayPreview(model, new Rect(x, y, width, height), dpi);
            UpdateCapturedOverlayAdorner(model, dpi, width, height);
            _capturedOverlayScene!.Width = width / dpi;
            _capturedOverlayScene.Height = height / dpi;
            if (showSpotify)
            {
                var displayCard = SpotifyOverlayLayout.Resolve(width, height, spec!.Position, spec.Transform);
                var displayRaster = SpotifyOverlayLayout.ResolveRenderBounds(width, height, spec.Position, spec.Transform);
                _spotifyPreview!.Width = displayRaster.Width / dpi;
                _spotifyPreview.Height = displayRaster.Height / dpi;
                Canvas.SetLeft(_spotifyPreview, displayRaster.X / dpi);
                Canvas.SetTop(_spotifyPreview, displayRaster.Y / dpi);
                _spotifyAdorner!.Width = width / dpi;
                _spotifyAdorner.Height = height / dpi;
                _spotifyAdorner.CardBounds = new Rect(displayCard.X / dpi, displayCard.Y / dpi,
                    displayCard.Width / dpi, displayCard.Height / dpi);
                _spotifyAdorner.RotationDegrees = spec.Transform is null ? 0 : SpotifyOverlayLayout.Normalize(width, height, spec.Transform).RotationDegrees;
                _spotifyAdorner.InvalidateVisual();
                _spotifyAdorner.IsVisible = model.IsSpotifyOverlaySelected && model.HasEditableSpotifyOverlay;
            }
            else { _spotifyPreview!.Clear(); _spotifyAdorner!.IsVisible = false; }
            // A four-times zoom must not allocate a monitor-sized offscreen
            // bitmap four times over. This caps preview raster size only.
            var rasterScale = Math.Min(1, 4096.0 / width);
            if (showSpotify) _spotifyPreview!.Update(spec!, model.CurrentTime.TotalSeconds,
                Math.Max(1, (int)Math.Round(width * rasterScale)), Math.Max(1, (int)Math.Round(height * rasterScale)));
            if (showSpotify && !_spotifyPreview!.HasCard && !_spotifyAdorner!.IsVisible && !showCamera && !showPeripherals) { HideSpotifyPreview(); return; }
            var handle = NativeHandleOf(_spotifyWindow);
            if (!_spotifyWindow.IsVisible || (handle != IntPtr.Zero && !IsWindowVisible(handle)))
            { _spotifyWindow.Show(this); _spotifyRaiseNeeded = true; }
            // LibVLC can create or reattach its child HWND after the owned card
            // window. Raise without activation so opening a clip does not wait
            // for unrelated hover controls to repair the stacking order.
            handle = NativeHandleOf(_spotifyWindow);
            var raised = _spotifyRaiseNeeded && handle != IntPtr.Zero;
            if (raised)
            {
                SetWindowPos(handle, HwndTop, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
                _spotifyRaiseNeeded = false;
            }
            _spotifyPerPixel?.ShowAndRefresh();
            // Anything that must sit above the video also has to sit above this
            // window, and only a raise can have disturbed them.
            if (raised) RestoreOverlayChrome();
        }
        catch (InvalidOperationException) { HideSpotifyPreview(); }
    }

    /// <summary>Puts the editor chrome back on top after the overlay window has
    /// claimed the top of the owner's z-band. Mirrors what the paused badge
    /// already does for the hover bar.</summary>
    private void RestoreOverlayChrome()
    {
        if (_recordingPausedOverlay is { IsVisible: true } badge) RepositionPausedOverlay(badge);
        RepositionEditorHoverControlsSafe(force: true);
    }

    private void HideSpotifyPreview()
    {
        _spotifyPerPixel?.Hide();
        _spotifyWindow?.Hide();
        _spotifyRaiseNeeded = true;
        // Hide both native surfaces before releasing pointer capture. Capture
        // loss can finish a drag and save its layout synchronously.
        EndSpotifyGesture();
    }

    private void UpdateCapturedOverlayPreview(MainWindowViewModel model, Rect videoBounds, double dpi)
    {
        var showCamera = model.HasCameraOverlayLayer && model.CameraOverlayLayerVisible && model.CameraOverlayTransform is not null;
        var showPeripherals = model.HasPeripheralOverlayLayer && model.PeripheralOverlayLayerVisible && model.PeripheralOverlayTransform is not null;
        if (!showCamera && !showPeripherals)
        { HideCapturedOverlayPreview(); return; }
        if (_capturedOverlayScene is null) return;
        var width = Math.Max(1, videoBounds.Width);
        var height = Math.Max(1, videoBounds.Height);
        if (showCamera)
        {
            var normalized = VideoOverlayLayout.Normalize(model.CameraOverlayTransform!, VideoOverlayLayout.CameraAspectRatio);
            var layerWidth = Math.Max(1, (int)Math.Round(width * normalized.Width));
            var layerHeight = Math.Max(1, (int)Math.Round(layerWidth / VideoOverlayLayout.CameraAspectRatio));
            _capturedCameraBounds = new Rect(width * normalized.X / dpi, height * normalized.Y / dpi,
                layerWidth / dpi, layerHeight / dpi);
            if (_capturedPlayback is null)
            {
                _capturedPlayback = new CapturedOverlayPlayback();
                _capturedPlayback.FrameReady += image => _capturedOverlayScene?.SetCamera(image, _capturedCameraBounds);
            }
            _capturedPlayback.Request(model.Settings.LibraryFolder, model.SelectedOverlayManifestCamera(), model.CurrentTime.TotalSeconds);
        }
        else { _capturedOverlayScene.ClearCamera(); _capturedCameraBounds = default; }
        if (showPeripherals)
        {
            var peripheralLayer = model.SelectedOverlayManifestPeripherals();
            var layout = peripheralLayer?.Source ?? KeyboardOverlayCatalog.QwertyCompact;
            if (_capturedInputPath != model.SelectedVideoPath)
            {
                _capturedInput = ClipInputIndex.Load(model.Settings.LibraryFolder, peripheralLayer);
                _capturedInputPath = model.SelectedVideoPath;
            }
            var aspect = KeyboardOverlayCatalog.Get(layout).AspectRatio;
            var normalized = VideoOverlayLayout.Normalize(model.PeripheralOverlayTransform!, aspect);
            var layerWidth = Math.Max(1, width * normalized.Width);
            _capturedPeripheralBounds = new Rect(width * normalized.X / dpi, height * normalized.Y / dpi,
                layerWidth / dpi, layerWidth / aspect / dpi);
            _capturedOverlayScene.SetPeripherals(layout, _capturedPeripheralBounds,
                ClipInputIndex.PressedAt(_capturedInput, model.CurrentTime.TotalSeconds));
        }
        else { _capturedOverlayScene.ClearPeripherals(); _capturedPeripheralBounds = default; }
        _capturedOverlayScene.IsVisible = true;
    }

    private Rect _capturedCameraBounds;

    private void UpdateCapturedOverlayAdorner(MainWindowViewModel model, double dpi, double width, double height)
    {
        if (_capturedAdorner is null) return;
        var bounds = model.IsCameraOverlaySelected ? _capturedCameraBounds
            : model.IsPeripheralOverlaySelected ? _capturedPeripheralBounds
            : default;
        _capturedAdorner.IsVisible = bounds.Width > 0 && bounds.Height > 0;
        if (!_capturedAdorner.IsVisible) return;
        _capturedAdorner.Width = width / dpi;
        _capturedAdorner.Height = height / dpi;
        _capturedAdorner.CardBounds = bounds;
        _capturedAdorner.InvalidateVisual();
    }

    /// <summary>Topmost-first: the keyboard draws over the camera, so it wins a
    /// pointer they both contain.</summary>
    private bool TryHitCapturedOverlay(MainWindowViewModel model, Point point, out string layer, out VideoOverlayManipulationMode mode)
    {
        foreach (var (name, bounds, present) in new[]
                 {
                     ("Peripherals", _capturedPeripheralBounds, model.HasPeripheralOverlayLayer && model.PeripheralOverlayLayerVisible),
                     ("Camera", _capturedCameraBounds, model.HasCameraOverlayLayer && model.CameraOverlayLayerVisible)
                 })
        {
            if (!present || bounds.Width <= 0 || bounds.Height <= 0) continue;
            if (!Services.CapturedOverlayHitTest.TryHit(bounds, point, out mode)) continue;
            layer = name;
            return true;
        }
        layer = string.Empty;
        mode = VideoOverlayManipulationMode.Move;
        return false;
    }

    private void HideCapturedOverlayPreview()
    {
        if (_capturedOverlayScene is not null)
        {
            _capturedOverlayScene.ClearCamera();
            _capturedOverlayScene.ClearPeripherals();
            _capturedOverlayScene.IsVisible = false;
        }
    }

    private void SpotifySurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_spotifySurface is null || ViewModel is not { } surfaceModel ||
            !e.GetCurrentPoint(_spotifySurface).Properties.IsLeftButtonPressed) return;
        // The Spotify card sits above the captured layers, so it is offered the
        // pointer first - but only where it actually is, which is what
        // TryHitTest distinguishes from empty canvas.
        var surfacePoint = e.GetPosition(_spotifySurface);
        var overCard = surfaceModel.HasEditableSpotifyOverlay && _spotifyAdorner is not null &&
                       _spotifyAdorner.TryHitTest(surfacePoint, out _);
        if (!overCard && BeginCapturedOverlayGesture(surfaceModel, surfacePoint, e)) return;
        if (surfaceModel is not { HasEditableSpotifyOverlay: true } model) { DeselectCapturedOverlaysFromSurface(surfaceModel); return; }
        var point = surfacePoint;
        var mode = model.IsSpotifyOverlaySelected && _spotifyAdorner is not null ? _spotifyAdorner.HitTest(point) : SpotifyOverlayDragMode.Move;
        model.IsSpotifyOverlaySelected = true;
        model.DeselectCapturedOverlays();
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

    /// <summary>Starts a camera or keyboard drag. Mirrors the Spotify gesture:
    /// the start transform is snapshotted, the drag runs unpersisted, and the
    /// layout is written once on release.</summary>
    private bool BeginCapturedOverlayGesture(MainWindowViewModel model, Point point, PointerPressedEventArgs e)
    {
        if (_spotifySurface is null || !TryHitCapturedOverlay(model, point, out var layer, out var mode)) return false;
        var start = layer == "Camera" ? model.CameraOverlayTransform : model.PeripheralOverlayTransform;
        if (start is null) return false;
        if (layer == "Camera") model.IsCameraOverlaySelected = true; else model.IsPeripheralOverlaySelected = true;
        model.OpenEditorSidebar(EditorSidebarSection.Overlays);
        _capturedGesture = new(model.SelectedVideoPath, layer, _spotifySurface.PointToScreen(point), start, mode,
            Math.Max(1, _spotifyVideoBounds.Width), Math.Max(1, _spotifyVideoBounds.Height));
        _spotifyGestureChanged = false;
        _spotifyPointer = e.Pointer;
        e.Pointer.Capture(_spotifySurface);
        e.Handled = true;
        return true;
    }

    private void DeselectCapturedOverlaysFromSurface(MainWindowViewModel model)
    {
        if (_capturedGesture is not null) return;
        model.DeselectCapturedOverlays();
    }

    private void SpotifySurface_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_spotifySurface is null) return;
        if (MoveCapturedOverlayGesture(e)) return;
        var point = e.GetPosition(_spotifySurface);
        // Hovering a captured layer shows its own grab cursor; without this the
        // camera and keyboard read as scenery rather than as draggable objects.
        if (_spotifyGesture is null && ViewModel is { } hoverModel &&
            !(hoverModel.HasEditableSpotifyOverlay && _spotifyAdorner is not null && _spotifyAdorner.TryHitTest(point, out _)) &&
            TryHitCapturedOverlay(hoverModel, point, out _, out var capturedMode))
        {
            _spotifySurface.Cursor = capturedMode switch
            {
                VideoOverlayManipulationMode.TopLeft or VideoOverlayManipulationMode.BottomRight => SpotifyNorthWestCursor,
                VideoOverlayManipulationMode.TopRight or VideoOverlayManipulationMode.BottomLeft => SpotifyNorthEastCursor,
                _ => SpotifyMoveCursor
            };
            return;
        }
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

    private bool MoveCapturedOverlayGesture(PointerEventArgs e)
    {
        if (_capturedGesture is not { } gesture || _spotifySurface is null) return false;
        if (ViewModel is not { } model || model.SelectedVideoPath != gesture.ClipPath) { EndCapturedOverlayGesture(); return true; }
        var screen = _spotifySurface.PointToScreen(e.GetPosition(_spotifySurface));
        // Same 2px dead zone as the Spotify drag, so a click that selects does
        // not also nudge the layout.
        if (!_spotifyGestureChanged && Math.Abs(screen.X - gesture.PointerStart.X) < 2 && Math.Abs(screen.Y - gesture.PointerStart.Y) < 2) return true;
        _spotifyGestureChanged = true;
        // VideoOverlayManipulation takes normalized deltas, unlike the Spotify
        // manipulation which takes screen pixels plus frame dimensions.
        var aspect = gesture.Layer == "Camera"
            ? VideoOverlayLayout.CameraAspectRatio
            : KeyboardOverlayCatalog.Get(model.SelectedOverlayManifestPeripherals()?.Source ?? KeyboardOverlayCatalog.QwertyCompact).AspectRatio;
        var transform = VideoOverlayManipulation.Apply(gesture.Start, gesture.Mode,
            (screen.X - gesture.PointerStart.X) / gesture.FrameWidth,
            (screen.Y - gesture.PointerStart.Y) / gesture.FrameHeight,
            gesture.FrameWidth / gesture.FrameHeight, aspect);
        if (gesture.Layer == "Camera") model.SetCameraOverlayTransform(transform, persist: false);
        else model.SetPeripheralOverlayTransform(transform, persist: false);
        e.Handled = true;
        return true;
    }

    private void EndCapturedOverlayGesture()
    {
        var gesture = _capturedGesture;
        var changed = _spotifyGestureChanged;
        _capturedGesture = null;
        _spotifyGestureChanged = false;
        var pointer = _spotifyPointer;
        _spotifyPointer = null;
        pointer?.Capture(null);
        if (changed && gesture is not null && ViewModel?.SelectedVideoPath == gesture.ClipPath)
            ViewModel.CommitCapturedOverlayTransform();
    }

    private void SpotifySurface_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_capturedGesture is not null)
        {
            MoveCapturedOverlayGesture(e);
            EndCapturedOverlayGesture();
            e.Handled = true;
            return;
        }
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
