using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _editorGraphicsCancellation;
    private Task? _editorGraphicsRecovery;
    private PlaybackSession? _editorGraphicsSession;
    private string? _editorGraphicsPath;
    private bool _editorGraphicsAttemptFailed;

    private void CancelEditorGraphicsRecovery() => _editorGraphicsCancellation?.Cancel();
    private void CheckEditorGraphicsRecoveryNavigation()
    {
        if (_editorGraphicsRecovery is not { IsCompleted: false }) return;
        if (ViewModel is not { IsEditorVisible: true } model || _playback != _editorGraphicsSession ||
            !string.Equals(model.SelectedVideoPath, _editorGraphicsPath, StringComparison.OrdinalIgnoreCase)) CancelEditorGraphicsRecovery();
    }
    private void BeginEditorGraphicsRecovery(PlaybackSession session, MainWindowViewModel model, TimeSpan position)
    {
        CancelEditorGraphicsRecovery(); _editorGraphicsCancellation?.Dispose();
        var cancellation = _editorGraphicsCancellation = new CancellationTokenSource();
        _editorGraphicsSession = session; _editorGraphicsPath = model.SelectedVideoPath;
        var path = model.SelectedVideoPath;
        _playbackStartCts?.Cancel(); _editorSeekCts?.Cancel();
        session.BeginGraphicsRecovery(position);
        _playbackTimer.Stop(); _playheadClock.Stop();
        model.IsEditorVideoLoading = true;
        model.EditorGraphicsRecoveryStatus = "Waiting for graphics device";
        _editorLoadError = _editorLoadErrorDetail = null;
        SyncEditorLoadingOverlay();
        AppLog.Info($"Editor graphics recovery started: {path}");
        _editorGraphicsRecovery = RecoverEditorGraphicsAsync(session, model, path, cancellation.Token);
    }

    private async Task RecoverEditorGraphicsAsync(PlaybackSession session, MainWindowViewModel model, string path, CancellationToken token)
    {
        bool Current() => !token.IsCancellationRequested && _playback == session && model.IsEditorVisible &&
            string.Equals(model.SelectedVideoPath, path, StringComparison.OrdinalIgnoreCase);
        try
        {
            await GraphicsRecovery.RunAsync(async (attempt, attemptToken) => {
                if (!await Dispatcher.UIThread.InvokeAsync(Current)) throw new OperationCanceledException(token);
                _editorGraphicsAttemptFailed = false;
                await session.RebuildGraphicsOutputAsync(path, attemptToken).ConfigureAwait(false);
                var output = session.Composition;
                session.PublishSceneAsync = async (time, current, sceneToken) => await Dispatcher.UIThread.InvokeAsync(() => {
                    if (!Current() || sceneToken.IsCancellationRequested || !current() || session.Composition != output) return false;
                    // Force retained camera, keyboard, Spotify and current edits
                    // through the complete scene before revealing this output.
                    UpdateSpotifyPreview();
                    return !_editorGraphicsAttemptFailed && UpdateNativeComposition(model, time);
                });
                var target = await Dispatcher.UIThread.InvokeAsync(() => model.CurrentTime);
                var landed = await session.PresentGraphicsRecoveryAsync(target, attemptToken).ConfigureAwait(false);
                if (landed.Outcome != PlaybackSeekOutcome.Completed || _editorGraphicsAttemptFailed || output?.HasPresentedPicture != true) return false;
                await session.RestoreGraphicsRateAsync(attemptToken).ConfigureAwait(false);
                var revealed = await Dispatcher.UIThread.InvokeAsync(() => {
                    if (!Current() || session.Composition != output) return false;
                    session.EndGraphicsRecovery();
                    model.EditorGraphicsRecoveryStatus = string.Empty;
                    model.IsEditorVideoLoading = false;
                    UpdateLayout(); return true;
                });
                if (!revealed) throw new OperationCanceledException(token);
                // Use the latest play intent and speed: changes during the
                // outage stand. Audio readers/volumes were retained throughout.
                var play = await Dispatcher.UIThread.InvokeAsync(() => model.IsPlaying);
                if (play) {
                    var started = await session.StartCoordinatedAsync(target, attemptToken, reusePresentedFrame: true).ConfigureAwait(false);
                    if (started.Outcome is not (EditorPlaybackStartOutcome.Playing or EditorPlaybackStartOutcome.AudioPending)) return false;
                }
                if (!await Dispatcher.UIThread.InvokeAsync(Current)) throw new OperationCanceledException(token);
                if (!await Dispatcher.UIThread.InvokeAsync(() => model.IsPlaying)) await Task.Run(session.Pause, attemptToken).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => {
                    if (Current()) { _playbackTimer.Start(); ResetPlayheadClockAfterSeek(model.CurrentTime); }
                });
                AppLog.Info("Editor graphics recovery restored complete composition and transport."); return true;
            }, token, (attempt, delay) => Dispatcher.UIThread.Post(() => {
                if (Current()) { model.IsEditorVideoLoading = true; model.EditorGraphicsRecoveryStatus = "Waiting for graphics device"; }
            }));
        }
        catch (EditorGraphicsRestartRequiredException error)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { if (Current()) { model.EditorGraphicsRecoveryStatus = string.Empty; ShowEditorLoadError("Restart ClypDat to restore video preview", error.Message); EditorLoadRetryButton.IsEnabled = false; } });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Editor graphics recovery failed.", error); }
        finally
        {
            session.EndGraphicsRecovery();
            await Dispatcher.UIThread.InvokeAsync(() => {
                if (_editorGraphicsSession == session && _editorGraphicsPath == path) model.EditorGraphicsRecoveryStatus = string.Empty;
            });
        }
    }
}
