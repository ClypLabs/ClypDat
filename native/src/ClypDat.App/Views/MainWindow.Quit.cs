using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using System.Diagnostics;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

// Every real exit (Alt+F4, tray Quit, update install, installer request,
// Windows sign-out/shutdown) goes through QuitAsync. It holds the process open
// behind the Closing Safely popup while ShutdownGuard reports work that would be
// lost, then waits for the recorder to finish its files before shutting down.
public sealed partial class MainWindow
{
    // The recorder drains saves and full-session muxing for up to 5 minutes
    // after it acks shutdown (CaptureWorkerHost); allow a little over that.
    private static readonly TimeSpan RecorderDrainLimit = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(15);
    // Below this the recorder is just exiting normally, not worth a popup.
    private static readonly TimeSpan RecorderQuietWait = TimeSpan.FromSeconds(3);
    private Task? _quitTask;
    private ClosingSafelyWindow? _closingSafelyWindow;
    private bool _quitForOsShutdown;

    public bool IsQuitting => _quitTask is not null;

    public Task QuitAsync(string reason)
    {
        if (_quitTask is not null)
        {
            _closingSafelyWindow?.Activate();
            return _quitTask;
        }
        return _quitTask = RunQuitAsync(reason);
    }

    // Wired to the desktop lifetime. Windows sign-out/shutdown arrives here as
    // WM_QUERYENDSESSION; so does a programmatic desktop.Shutdown(), which only
    // RunQuitAsync issues and which is let through untouched.
    internal void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (AllowRealClose) return;
        var recorderBusy = ViewModel?.FullSessionRecordingEnabled == true && ViewModel.IsReplayRecording;
        if (!ShutdownGuard.IsBusy && !recorderBusy && _quitTask is null)
        {
            AppLog.Info($"[Quit] reason=os-session-end, idle; allowing.");
            AllowRealClose = true;
            UncleanExitRecovery.MarkCleanExit();
            _ = ShutdownCaptureWorkerAsync();
            return;
        }

        e.Cancel = true;
        _quitForOsShutdown = true;
        _closingSafelyWindow?.BlockShutdown(ShutdownBlockMessage);
        _ = QuitAsync("os-session-end");
    }

    private const string ShutdownBlockMessage = "ClypDat is closing safely so your clips aren't lost.";

    private async Task RunQuitAsync(string reason)
    {
        AppLog.Info($"[Quit] reason={reason}, busy=[{string.Join(", ", ShutdownGuard.ActiveLabels)}]");
        var clock = Stopwatch.StartNew();
        var quitAnyway = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (IsVisible)
            {
                SaveWindowBounds();
                ViewModel?.SaveSettings();
                HideToTray();
            }
            // Dialogs (export/share progress, confirms, the scrim behind them)
            // are hidden, not closed: closing one can cancel the very work the
            // quit is waiting on. They close themselves when that work ends.
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                foreach (var window in lifetime.Windows.ToArray())
                {
                    if (window is MainWindow or ClosingSafelyWindow || !window.IsVisible) continue;
                    window.Hide();
                }
            }

            if (ShutdownGuard.IsBusy)
            {
                ShowClosingSafely(quitAnyway);
                await Task.WhenAny(ShutdownGuard.WaitIdleAsync(), quitAnyway.Task);
                AppLog.Info($"[Quit] work finished after {clock.Elapsed.TotalSeconds:0.0}s (quitAnyway={quitAnyway.Task.IsCompleted}).");
            }

            await ShutdownCaptureWorkerAsync();
            if (!quitAnyway.Task.IsCompleted && _replayBuffer is CaptureWorkerProxy worker)
            {
                var exited = await worker.WaitForWorkerExitAsync(RecorderQuietWait);
                if (!exited)
                {
                    ShowClosingSafely(quitAnyway);
                    _closingSafelyWindow?.SetExtraTask("Finishing recording files");
                    using var cancel = new CancellationTokenSource();
                    var drain = worker.WaitForWorkerExitAsync(RecorderDrainLimit, cancel.Token);
                    await Task.WhenAny(drain, quitAnyway.Task);
                    cancel.Cancel();
                    exited = drain.IsCompletedSuccessfully && drain.Result;
                }
                AppLog.Info($"[Quit] recorder exited={exited} after {clock.Elapsed.TotalSeconds:0.0}s.");
            }
        }
        catch (Exception error)
        {
            AppLog.Error("[Quit] safe-close wait failed; exiting anyway.", error);
        }

        AppLog.Info($"[Quit] exiting after {clock.Elapsed.TotalSeconds:0.0}s.");
        UncleanExitRecovery.MarkCleanExit();
        AllowRealClose = true;
        _closingSafelyWindow?.CloseForExit();
        _closingSafelyWindow = null;
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Environment.Exit(0);
        }
    }

    private void ShowClosingSafely(TaskCompletionSource quitAnyway)
    {
        if (_closingSafelyWindow is not null) return;
        var window = new ClosingSafelyWindow();
        window.QuitAnywayRequested += () =>
        {
            AppLog.Info("[Quit] user chose Quit anyway.");
            quitAnyway.TrySetResult();
        };
        _closingSafelyWindow = window;
        window.Show();
        if (_quitForOsShutdown) Dispatcher.UIThread.Post(() => window.BlockShutdown(ShutdownBlockMessage));
    }

    // Same teardown as closing to tray: the window goes away, but playback
    // (LibVLC + the audio mixer) doesn't follow window visibility by itself.
    private void HideToTray()
    {
        ViewModel?.SaveSelectedClipEditState();
        if (ViewModel?.IsVideoFullscreen == true) ExitVideoFullscreen();
        StopEditorPlayback(stopMode: PlaybackStopMode.Background);
        // Closing to tray is a navigation reset, not a suspended
        // editor. Reopening ClypDat must always return to Library.
        ViewModel?.CloseEditor();
        Hide();
        ShowInTaskbar = false;
    }
}
