using Avalonia.Controls;
using Avalonia.Layout;

namespace ClypDat.App.Views;

public sealed partial class MainWindow
{
#if CLYPDAT_LINUX
    private Avalonia.FreeDesktop.IPortalParentLease? _kdeShortcutParent;
    private bool _kdeShortcutCleanupRegistered;
    private async Task ConfigureKdeShortcutsAsync()
    {
        try {
            if (_replayBuffer is not ClypDat.App.Services.CaptureWorkerProxy proxy) return;
            proxy.LinuxBindingsChanged -= ApplyKdeBindings;
            proxy.LinuxBindingsChanged += ApplyKdeBindings;
            var provider = PlatformImpl?.TryGetFeature(typeof(Avalonia.FreeDesktop.IPortalParentProvider)) as Avalonia.FreeDesktop.IPortalParentProvider;
            var parent = provider is null ? null : await provider.AcquirePortalParentAsync();
            if (_kdeShortcutParent is not null) await _kdeShortcutParent.DisposeAsync();
            _kdeShortcutParent = parent;
            if (!_kdeShortcutCleanupRegistered) {
                _kdeShortcutCleanupRegistered = true;
                Closed += async (_, _) => { if (_kdeShortcutParent is not null) await _kdeShortcutParent.DisposeAsync(); _kdeShortcutParent = null; };
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await proxy.ConfigureLinuxShortcutsAsync(parent?.Handle ?? string.Empty, timeout.Token);
        } catch (Exception error) { await ShowMessageAsync("KDE shortcuts", error.Message); }
    }
    private void ApplyKdeBindings(IReadOnlyDictionary<string, string> bindings) => ViewModel?.ApplyLinuxBindings(bindings);
#endif
    private void UpdateLinuxEffectControls(ClypDat.App.ViewModels.MainWindowViewModel model, TimeSpan position)
    {
        if (!model.IsEditorVisible || !IsVisible || IsEditorSurfaceCovered || !model.IsEditorVideoAreaVisible) {
            if (_timedEffectLayer is not null) _timedEffectLayer.IsVisible = false;
            return;
        }
        _timedEffectLayer ??= new ClypDat.App.Controls.TimedEffectLayer();
        var host = model.IsVideoFullscreen ? FullscreenVideoHost : EditorVideoHost;
        if (_timedEffectLayer.Parent != host) {
            if (_timedEffectLayer.Parent is Panel previous) previous.Children.Remove(_timedEffectLayer);
            host.Children.Add(_timedEffectLayer);
        }
        var scale = Math.Min(EditorVideoView.Bounds.Width / Math.Max(1, model.SelectedSourceWidth), EditorVideoView.Bounds.Height / Math.Max(1, model.SelectedSourceHeight));
        var crop = model.ActiveCropRect;
        _timedEffectLayer.Width = (crop?.Width ?? model.SelectedSourceWidth) * scale;
        _timedEffectLayer.Height = (crop?.Height ?? model.SelectedSourceHeight) * scale;
        _timedEffectLayer.HorizontalAlignment = HorizontalAlignment.Center;
        _timedEffectLayer.VerticalAlignment = VerticalAlignment.Center;
        _timedEffectLayer.RenderTransform = EditorVideoView.RenderTransform;
        _timedEffectLayer.RenderTransformOrigin = EditorVideoView.RenderTransformOrigin;
        _timedEffectLayer.ZIndex = 10; _timedEffectLayer.IsVisible = true;
        _timedEffectLayer.Update(model, position);
    }
    private Control? _linuxPlaybackControls;
    private void UpdateLinuxPlaybackControls()
    {
        if (ViewModel is null || !IsVisible || !ViewModel.IsEditorVisible || _playback is null || IsEditorSurfaceCovered)
        { if (_linuxPlaybackControls is not null) _linuxPlaybackControls.IsVisible = false; return; }
        _linuxPlaybackControls ??= BuildPlaybackBarLayout();
        var host = ViewModel.IsVideoFullscreen ? FullscreenVideoHost : EditorVideoHost;
        if (_linuxPlaybackControls.Parent != host)
        {
            if (_linuxPlaybackControls.Parent is Panel previous) previous.Children.Remove(_linuxPlaybackControls);
            _linuxPlaybackControls.HorizontalAlignment = HorizontalAlignment.Stretch;
            _linuxPlaybackControls.VerticalAlignment = VerticalAlignment.Bottom;
            _linuxPlaybackControls.ZIndex = 20;
            host.Children.Add(_linuxPlaybackControls);
        }
        _linuxPlaybackControls.IsVisible = host.IsPointerOver || _linuxPlaybackControls.IsPointerOver;
    }
}
