using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.Controls;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

public sealed partial class MainWindow
{
    private Window? _spotifyWindow;
    private SpotifyCardPreview? _spotifyPreview;
    private ServerPerPixelOverlay? _spotifyPerPixel;
    private SpotifyRenderSpec? _spotifyPreviewSpec;
    private string? _spotifyPreviewPath;
    private bool _spotifyPreviewDirty = true;
    private void InitializeSpotifyPreview()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        timer.Tick += (_, _) => UpdateSpotifyPreview();
        Opened += (_, _) => timer.Start();
        Closed += (_, _) => { timer.Stop(); _spotifyPerPixel?.Dispose(); _spotifyWindow?.Close(); _spotifyPreview?.Dispose(); };
    }
    private void UpdateSpotifyPreview()
    {
        var model = ViewModel;
        if (model is null || !model.IsEditorVisible || !IsVisible || WindowState == WindowState.Minimized || IsEditorSurfaceCovered || _playback is null)
        { _spotifyPerPixel?.Hide(); _spotifyWindow?.Hide(); return; }
        if (_spotifyPreviewDirty || _spotifyPreviewPath != model.SelectedVideoPath)
        {
            _spotifyPreviewSpec = model.SpotifyPreviewSpec();
            _spotifyPreviewPath = model.SelectedVideoPath;
            _spotifyPreviewDirty = false;
        }
        if (_spotifyPreviewSpec is not { } original || !model.Settings.SpotifyOverlayEnabled || model.SelectedSourceWidth <= 0)
        { _spotifyPerPixel?.Hide(); _spotifyWindow?.Hide(); return; }
        try
        {
            var spec = original with { Position = model.Settings.SpotifyOverlayPosition, Font = SpotifyOverlayCardRenderer.ResolveFont(),
                DynamicBackground = model.Settings.SpotifyOverlayDynamicBackground };
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
            var visibleRight = Math.Min(x + videoWidth, hostBottom.X);
            var visibleBottom = Math.Min(y + videoHeight, hostBottom.Y);
            x = Math.Max(x, hostTop.X); y = Math.Max(y, hostTop.Y);
            videoWidth = visibleRight - x; videoHeight = visibleBottom - y;
            if (videoWidth <= 0 || videoHeight <= 0) { _spotifyPerPixel?.Hide(); _spotifyWindow?.Hide(); return; }
            var width = Math.Max(1, (int)videoWidth);
            var height = Math.Max(1, (int)videoHeight);
            var scale = SpotifyOverlayCardRenderer.Scale(width, height);
            var cardWidth = Math.Ceiling(406 * scale);
            var cardHeight = Math.Ceiling(140 * scale);
            if (spec.Position.EndsWith("Right", StringComparison.OrdinalIgnoreCase)) x += videoWidth - cardWidth;
            y += spec.Position.StartsWith("Top", StringComparison.OrdinalIgnoreCase) ? 14 * height / 1080.0 :
                spec.Position.StartsWith("Center", StringComparison.OrdinalIgnoreCase) ? (videoHeight - cardHeight) / 2 : videoHeight - cardHeight - 14 * height / 1080.0;
            if (_spotifyWindow is null)
            {
                _spotifyPreview = new();
                _spotifyWindow = new Window { WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, CanResize = false,
                    ShowActivated = false, Topmost = false, Background = Brushes.Transparent,
                    TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }, Content = _spotifyPreview };
                _spotifyWindow.Opened += (_, _) =>
                {
                    var handle = NativeHandleOf(_spotifyWindow);
                    var style = (long)GetWindowLongPtr(handle, GwlExStyle);
                    SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(style | WsExNoActivate | WsExTransparent));
                    if (WindowsPlatformProfile.IsServer())
                    {
                        _spotifyPerPixel = new(_spotifyWindow, _spotifyPreview);
                        WindowTransparencyFallback.ApplyInputSurfaceIfNeeded(_spotifyWindow);
                    }
                };
            }
            _spotifyWindow.Position = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
            _spotifyWindow.Width = cardWidth / _spotifyWindow.RenderScaling;
            _spotifyWindow.Height = cardHeight / _spotifyWindow.RenderScaling;
            _spotifyPreview!.Update(spec, model.CurrentTime.TotalSeconds, width, height);
            if (!_spotifyWindow.IsVisible) _spotifyWindow.Show(this);
            _spotifyPerPixel?.ShowAndRefresh();
        }
        catch (InvalidOperationException) { _spotifyPerPixel?.Hide(); _spotifyWindow?.Hide(); }
    }
}
