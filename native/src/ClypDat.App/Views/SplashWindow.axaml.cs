using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

/// <summary>
/// Shown before the main window on a normal launch, and closed once the app is
/// actually usable: the update check has answered and the library's cached
/// contents are loaded. The main window stays hidden until then, so the user
/// never gets a half-built library to click on.
///
/// Deliberately does no work of its own beyond painting - every stage is driven
/// by <see cref="RunAsync"/> so the sequence is one readable list rather than
/// state spread across event handlers.
/// </summary>
public sealed partial class SplashWindow : Window
{
    // A launch must never be held up by something that is not the app: a
    // GitHub check against a dead network, a library on a disconnected share.
    // Past this, the window opens with whatever is ready.
    private static readonly TimeSpan UpdateCheckBudget = TimeSpan.FromSeconds(6);
    // The library is explicitly NOT something the user waits on. This is only
    // long enough for the cached first rows on a normal disk; a cache miss, a
    // network share or a cold spinning disk hands over anyway and finishes
    // filling the grid behind the open window.
    private static readonly TimeSpan LibraryBudget = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan FadeStep = TimeSpan.FromMilliseconds(16);

    private CancellationTokenSource? _skip;

    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AppUpdateService.CurrentVersion.ToString(3)}";
        // WindowStartupLocation can use the monitor of the last active window.
        // The updater is launch-critical, so it always belongs on Windows'
        // primary display instead.
        var primary = DesktopMonitorService.Resolve(null);
        Position = new PixelPoint(
            primary.X + Math.Max(0, (primary.Width - (int)Width) / 2),
            primary.Y + Math.Max(0, (primary.Height - (int)Height) / 2));
        // DWM rounds the composited frame itself, so the corners come out
        // smooth on an opaque window - no per-pixel transparency, and none of
        // the layered/ANGLE surface trouble that comes with it.
        Opened += (_, _) => StartupWindowPresentation.TryRoundCorners(this);
    }

    public void SetStage(string text) => StageText.Text = text;

    /// <summary>Null shows the indeterminate sweep; a fraction shows real progress.</summary>
    public void SetProgress(double? fraction)
    {
        if (fraction is null)
        {
            Bar.IsIndeterminate = true;
            return;
        }
        Bar.IsIndeterminate = false;
        Bar.Value = Math.Clamp(fraction.Value, 0, 1);
    }

    /// <summary>
    /// Runs the startup stages, then hands back the update the check found (if
    /// any) so the main window can offer it without asking GitHub again.
    /// </summary>
    public async Task<AppUpdateInfo?> RunAsync(Task essentialsLoaded,
        Func<AppUpdateInfo, bool> shouldInstall, Func<Task> onInstallerStarted)
    {
        using var budget = new CancellationTokenSource(TotalBudget);
        var update = await CheckForUpdateAsync(budget.Token).ConfigureAwait(true);
        if (update is not null && shouldInstall(update) && await InstallAsync(update).ConfigureAwait(true))
        {
            // The installer is running and waiting on this process to exit; the
            // window behind this loader is never going to be shown.
            SetStage("Restarting ClypDat");
            await onInstallerStarted().ConfigureAwait(true);
            return update;
        }

        SetStage("Loading library");
        SetProgress(null);
        try
        {
            await essentialsLoaded.WaitAsync(LibraryBudget, budget.Token).ConfigureAwait(true);
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            AppLog.Info("Startup: library reveal timed out; opening window while loading continues behind it.");
        }
        catch (Exception error)
        {
            AppLog.Error("Startup: initial library load failed.", error);
        }

        SetStage("Ready");
        SetProgress(1);
        // Long enough to read, short enough that nobody waits on it.
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(true);
        return update;
    }

    private void SkipButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SkipButton.IsEnabled = false;
        _skip?.Cancel();
    }

    /// <summary>
    /// Downloads and launches the installer, reporting real progress. Returns
    /// false when the update did not happen - skipped, or it failed - in which
    /// case startup carries on and the usual dialog offers it after launch.
    /// </summary>
    private async Task<bool> InstallAsync(AppUpdateInfo update)
    {
        // No budget here on purpose: an install is the user watching a real
        // transfer, not the app being slow, and the Not now button is the way
        // out of it.
        _skip = new CancellationTokenSource();
        SkipButton.IsVisible = true;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(report =>
            {
                SetStage(report.Status);
                SetProgress(report.Percentage);
            });
            SetStage($"Downloading update {update.LatestVersion.ToString(3)}");
            SetProgress(0);
            await AppUpdateService.DownloadAndRestartAsync(update, progress, _skip.Token).ConfigureAwait(true);
            AppLog.Info($"Startup: loader installed update {update.LatestVersion.ToString(3)}; handing over to the installer.");
            return true;
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Startup: user skipped the update at launch.");
            return false;
        }
        catch (Exception error)
        {
            AppLog.Error("Startup: update install failed; continuing into the app.", error);
            SetStage("Update failed - starting anyway");
            return false;
        }
        finally
        {
            SkipButton.IsVisible = false;
            _skip?.Dispose();
            _skip = null;
        }
    }

    private async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken token)
    {
        SetStage("Checking for updates");
        SetProgress(null);
        try
        {
            using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
            check.CancelAfter(UpdateCheckBudget);
            var update = await AppUpdateService.CheckAsync(check.Token).ConfigureAwait(true);
            if (update is not null) SetStage($"Update {update.LatestVersion.ToString(3)} available");
            return update;
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            AppLog.Info("Startup: update check did not answer in time; continuing.");
            return null;
        }
        catch (Exception error)
        {
            AppLog.Error("Startup: update check failed.", error);
            return null;
        }
    }

    /// <summary>Fades out and closes. Never throws - a splash that will not go away is a bug worth swallowing.</summary>
    public async Task FadeOutAndCloseAsync()
    {
        try
        {
            for (var opacity = 1d; opacity > 0; opacity -= 0.08)
            {
                Opacity = opacity;
                await Task.Delay(FadeStep).ConfigureAwait(true);
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Startup: splash fade failed.", error);
        }
        finally
        {
            Close();
        }
    }
}
