using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using ClypDat.App.Controls;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _effectPreviewCancellation;
    private string? _effectPreviewKey;
    private string? _effectProxy;
    private string? _effectProxySource;
    private bool _effectPreviewPending;
    private string _effectPreviewStatus = "";
    private TextBlock? _effectStatus;
    private Button? _effectPreviewButton;
    private TimedEffectFrame? _effectFrame;
    private double _effectFrameTime;
    private ClypDat.App.Controls.TimedEffectSurface? _timedEffectSurface;

    private void InitializeTimedEffectPreview()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => CheckTimedEffectPreview();
        Opened += (_, _) => timer.Start();
        Closed += (_, _) => { timer.Stop(); _effectPreviewCancellation?.Cancel(); if (_effectProxy is not null) AudioCapturePipeline.TryDelete(_effectProxy); };
    }

    private async void CheckTimedEffectPreview()
    {
        var model = ViewModel;
        if (model is null || !model.IsEditorVisible || model.IsExporting || _playback is null || model.SelectedSourceWidth <= 0 || _playback.LoadedPath != model.SelectedVideoPath)
        { _effectPreviewCancellation?.Cancel(); _effectPreviewKey = null; return; }
        var hasEffects = model.TextEffects.Any(e => e.Visible) || model.BlurEffects.Any(e => e.Visible);
        if (!hasEffects && _effectProxySource != model.SelectedVideoPath && !_effectPreviewPending) return;
        var key = JsonSerializer.Serialize(new { model.SelectedVideoPath, model.TextEffects, model.BlurEffects, model.ClipCropMode,
            model.CameraOverlayTransform, model.PeripheralOverlayTransform, model.SpotifyOverlayTransform,
            model.CameraOverlayLayerVisible, model.PeripheralOverlayLayerVisible, model.SpotifyOverlayLayerVisible,
            model.Settings.SpotifyOverlayDynamicBackground, model.Settings.SpotifyOverlayPosition });
        if (_effectPreviewPending && model.IsPlaying) { _playback.Pause(); model.IsPlaying = false; }
        if (key == _effectPreviewKey && (!_effectPreviewPending || Math.Abs(model.CurrentTime.TotalSeconds - _effectFrameTime) < .001)) return;
        _effectPreviewKey = key;
        _effectPreviewCancellation?.Cancel();
        _effectPreviewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _effectPreviewCancellation = cancellation;
        var token = cancellation.Token;
        var session = _playback;
        var source = model.SelectedVideoPath;
        var resume = model.IsPlaying;
        session.Pause(); model.IsPlaying = false;
        _effectPreviewPending = true;
        _effectFrameTime = model.CurrentTime.TotalSeconds;
        var frameTime = _effectFrameTime;
        if (_effectFrame is not null) { _effectFrame.Bitmap?.Dispose(); _effectFrame.Bitmap = null; }
        _effectPreviewStatus = "Preparing text / blur preview…";
        string? output = null;
        try
        {
            await Task.Delay(350, token);
            if (hasEffects)
            {
                output = TimedEffectPreview.WorkPath();
                var framePath = Path.ChangeExtension(output, ".png");
                try
                {
                    await model.RenderEffectPreviewAsync(framePath, token, frameTime);
                    token.ThrowIfCancellationRequested();
                    if (_effectFrame is not null)
                    {
                        _effectFrame.Bitmap = new Bitmap(framePath);
                        _effectFrame.SourceFraction = model.ActiveCropRect is { } crop
                            ? new Rect((double)crop.X / model.SelectedSourceWidth, (double)crop.Y / model.SelectedSourceHeight, (double)crop.Width / model.SelectedSourceWidth, (double)crop.Height / model.SelectedSourceHeight)
                            : new Rect(0, 0, 1, 1);
                        _effectFrame.InvalidateVisual();
                    }
                }
                finally { AudioCapturePipeline.TryDelete(framePath); }
                await model.RenderEffectPreviewAsync(output, token);
            }
            token.ThrowIfCancellationRequested();
            if (source != model.SelectedVideoPath || session != _playback) return;
            await session.ReplaceVideoAsync(source, output ?? source, model.CurrentTime, resume, token);
            token.ThrowIfCancellationRequested();
            var previous = _effectProxy;
            _effectProxy = output; output = null;
            _effectProxySource = hasEffects ? source : null;
            _effectPreviewPending = false;
            _effectPreviewStatus = "";
            model.IsPlaying = resume;
            ApplyEditorEffectPreview();
            if (previous is not null) AudioCapturePipeline.TryDelete(previous);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_effectPreviewKey == key)
            {
                _effectPreviewStatus = error.Message;
                AppLog.Error("Effect preview failed", error);
            }
        }
        finally { if (output is not null) AudioCapturePipeline.TryDelete(output); }
    }

    private bool ComposedEffectPreview => _effectProxySource == ViewModel?.SelectedVideoPath || _effectPreviewPending;
}
