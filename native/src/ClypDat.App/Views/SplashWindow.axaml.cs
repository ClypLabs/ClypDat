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
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FadeStep = TimeSpan.FromMilliseconds(16);

    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AppUpdateService.CurrentVersion.ToString(3)}";
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
    public async Task<AppUpdateInfo?> RunAsync(Task essentialsLoaded)
    {
        using var budget = new CancellationTokenSource(TotalBudget);
        var update = await CheckForUpdateAsync(budget.Token).ConfigureAwait(true);

        SetStage("Loading your library");
        SetProgress(null);
        try
        {
            await essentialsLoaded.WaitAsync(budget.Token).ConfigureAwait(true);
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            AppLog.Info("Startup: library was still loading when the splash budget ran out; opening anyway.");
        }
        catch (Exception error)
        {
            AppLog.Error("Startup: initial library load failed.", error);
        }

        SetStage("Ready");
        SetProgress(1);
        return update;
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
            if (update is not null)
            {
                // Reported, not installed. Replacing the running app is the
                // user's call, and the main window already owns that dialog.
                SetStage($"Update {update.LatestVersion.ToString(3)} available");
                await Task.Delay(TimeSpan.FromMilliseconds(700), token).ConfigureAwait(true);
            }
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
