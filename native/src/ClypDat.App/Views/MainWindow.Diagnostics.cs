using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

public sealed partial class MainWindow
{
    private async Task ShowSendDiagnosticsAsync()
    {
        if (ViewModel is null) return;
        if (!ViewModel.ClypDatAccountIsConnected)
        {
            if (await ShowModalDialogAsync<bool>(CreateDialog("Link an account to send diagnostics",
                "Connect your ClypDat account in Settings > Connected Accounts, then return here to send your report.",
                true, "Open settings")))
            {
                ViewModel.CloseHelp(restoreEditor: false);
                ViewModel.OpenSettings();
                ViewModel.SelectedSettingsSection = "Connected Accounts";
            }
            return;
        }

        var (dialog, body) = CreateChromelessDialog("Send diagnostics", centerTitle: false);
        dialog.Width = 560;
        if (body is StackPanel stack) stack.Spacing = 14;
        var message = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, MaxHeight = 200, MaxLength = 2000,
            PlaceholderText = "What happened? What were you doing when it happened?"
        };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = AppThemeService.Brush("TextSubtleBrush", "#9FB2C6") };
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, IsVisible = false };
        var receipt = new TextBox { IsReadOnly = true, IsVisible = false };
        var send = new Button { Content = "Send diagnostics", Classes = { "primaryButton" }, IsEnabled = false };
        var close = new Button { Content = "Cancel" };
        body.Children.Add(new TextBlock
        {
            Text = "Describe the issue", FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = AppThemeService.Brush("TextStrongBrush", "#EDF4FB")
        });
        body.Children.Add(message);
        body.Children.Add(new TextBlock
        {
            Text = "Sends your description, recent logs with common personal details redacted, capture health, and OS details to ClypDat's private support inbox. Your linked account identifies the report. Recordings are not included. Reports expire after 30 days.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = AppThemeService.Brush("TextSubtleBrush", "#9FB2C6")
        });
        body.Children.Add(progressBar);
        body.Children.Add(status);
        body.Children.Add(receipt);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10, Children = { close, send }
        });

        using var cancellation = new CancellationTokenSource();
        var closed = false;
        var uploading = false;
        string? bundlePath = null;
        string? submittedMessage = null;
        var reportId = Guid.NewGuid();
        message.TextChanged += (_, _) => send.IsEnabled = !uploading && message.Text?.Trim().Length >= 10;
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => { closed = true; cancellation.Cancel(); };
        var token = cancellation.Token;
        send.Click += async (_, _) =>
        {
            if (uploading) return;
            uploading = true;
            send.IsEnabled = false;
            message.IsEnabled = false;
            progressBar.IsVisible = true;
            progressBar.IsIndeterminate = true;
            status.Text = "Preparing recent diagnostics…";
            try
            {
                var text = message.Text!.Trim();
                if (submittedMessage is not null && submittedMessage != text) reportId = Guid.NewGuid();
                submittedMessage = text;
                var accountToken = await ViewModel.GetDiagnosticUploadTokenAsync(token);
                bundlePath ??= await Task.Run(() => CaptureDiagnosticBundle.CreateForUpload(_replayBuffer, _cs2GsiListener), token);
                token.ThrowIfCancellationRequested();
                progressBar.IsIndeterminate = false;
                var progress = new Progress<double>(percent =>
                {
                    if (closed || !uploading) return;
                    progressBar.Value = percent;
                    status.Text = percent < 100 ? $"Sending diagnostics… {percent:0}%" : "Waiting for confirmation…";
                });
                var id = await DiagnosticUploadService.SendAsync(accountToken, bundlePath, text, reportId, progress, token);
                if (closed) return;
                status.Text = "Diagnostics received. Keep this report ID if you contact support.";
                receipt.Text = id;
                receipt.IsVisible = true;
                progressBar.IsVisible = false;
                send.IsVisible = false;
                close.Content = "Done";
            }
            catch (OperationCanceledException)
            {
                if (!closed) status.Text = "Upload timed out. Retry to confirm whether the report was received.";
            }
            catch (Exception error)
            {
                AppLog.Error("Send diagnostics failed", error);
                if (!closed) status.Text = error.Message;
            }
            finally
            {
                uploading = false;
                if (!closed && send.IsVisible)
                {
                    progressBar.IsVisible = false;
                    send.Content = "Retry";
                    send.IsEnabled = true;
                    message.IsEnabled = true;
                }
            }
        };
        await ShowModalDialogAsync<bool>(dialog);
    }
}
