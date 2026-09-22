using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views;

/// <summary>
/// One page that walks through making a Spotify developer app of your own, so
/// the optional Spotify sign-in is not bound by the five-account cap on an
/// app Spotify has not approved. Every link opens the right Spotify page;
/// every value Spotify's form wants is one Copy press away; and the Client ID
/// comes back from the clipboard on its own when the window is returned to.
/// </summary>
public sealed partial class SpotifyOwnAppDialog : Window
{
    private const string DashboardUrl = "https://developer.spotify.com/dashboard";
    private const string CreateAppUrl = "https://developer.spotify.com/dashboard/create";
    private const string AppName = "ClypDat";
    private const string AppDescription = "Shows the song playing on my ClypDat clips";

    private readonly MainWindowViewModel? _viewModel;
    private DispatcherTimer? _copiedTimer;
    // What the clipboard held when last looked at. Returning to the window with
    // the same text is not a new copy, so a Client ID the user cleared is not
    // put straight back.
    private string? _lastClipboard;

    // Avalonia's XAML loader needs a parameterless constructor to accept this
    // as a top-level control; the one below is what actually opens.
    public SpotifyOwnAppDialog() => InitializeComponent();

    public SpotifyOwnAppDialog(MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        AppNameText.Text = AppName;
        AppDescriptionText.Text = AppDescription;
        RedirectUriText.Text = SpotifyNowPlayingService.RedirectUri;
        if (!string.IsNullOrEmpty(viewModel.SpotifyOwnAppClientId)) ClientIdBox.Text = viewModel.SpotifyOwnAppClientId;
        UpdateClientIdState(fromClipboard: false);
        Activated += (_, _) => _ = PickUpClientIdAsync();
        Closed += (_, _) =>
        {
            _copiedTimer?.Stop();
            // Closing mid sign-in stops waiting for the browser.
            if (viewModel.SpotifyConnectBusy) viewModel.CancelSpotifySignIn();
        };
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) { AppLog.Error($"Spotify setup: opening {url} failed.", error); }
    }

    private void OpenDashboard_OnClick(object? sender, RoutedEventArgs e) => OpenUrl(DashboardUrl);
    private void OpenCreate_OnClick(object? sender, RoutedEventArgs e) => OpenUrl(CreateAppUrl);

    private void OpenAppSettings_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SpotifyNowPlayingService.NormaliseClientId(ClientIdBox.Text) is { } id) OpenUrl($"{DashboardUrl}/{id}");
    }

    private async void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string which } || Clipboard is not { } clipboard) return;
        var (label, value) = which switch
        {
            "name" => ("App name", AppName),
            "description" => ("App description", AppDescription),
            _ => ("Redirect URI", SpotifyNowPlayingService.RedirectUri),
        };
        try
        {
            await clipboard.SetTextAsync(value);
            // Our own copy is not a Client ID coming back.
            _lastClipboard = value;
            ShowCopied($"{label} copied - paste it into Spotify's form.");
        }
        catch (Exception error)
        {
            AppLog.Error("Spotify setup: copying to the clipboard failed.", error);
            ShowCopied("Couldn't reach the clipboard. Select the text and copy it instead.");
        }
    }

    private void ShowCopied(string message)
    {
        CopiedText.Text = message;
        CopiedText.IsVisible = true;
        _copiedTimer?.Stop();
        _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _copiedTimer.Tick += (_, _) => { CopiedText.IsVisible = false; _copiedTimer?.Stop(); };
        _copiedTimer.Start();
    }

    // Coming back from the browser: a Client ID on the clipboard is almost
    // certainly the one just copied from the new app's page. Only read while
    // this window is open, and only when it regains focus.
    private async Task PickUpClientIdAsync()
    {
        if (Clipboard is not { } clipboard) return;
        if (SpotifyNowPlayingService.NormaliseClientId(ClientIdBox.Text) is not null) return;
        string? text;
        try { text = await clipboard.TryGetTextAsync(); }
        catch (Exception error)
        {
            AppLog.Error("Spotify setup: reading the clipboard failed.", error);
            return;
        }
        if (text is null || string.Equals(text, _lastClipboard, StringComparison.Ordinal)) return;
        _lastClipboard = text;
        if (SpotifyNowPlayingService.NormaliseClientId(text) is not { } id) return;
        ClientIdBox.Text = id;
        UpdateClientIdState(fromClipboard: true);
    }

    private async void Paste_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } clipboard) return;
        try
        {
            var text = await clipboard.TryGetTextAsync();
            if (text is not null) ClientIdBox.Text = text.Trim();
        }
        catch (Exception error) { AppLog.Error("Spotify setup: reading the clipboard failed.", error); }
    }

    private void ClientIdBox_OnTextChanged(object? sender, TextChangedEventArgs e) => UpdateClientIdState(fromClipboard: false);

    private void UpdateClientIdState(bool fromClipboard)
    {
        var text = ClientIdBox.Text?.Trim();
        var id = SpotifyNowPlayingService.NormaliseClientId(text);
        SignInButton.IsEnabled = id is not null;
        OpenAppSettingsButton.IsEnabled = id is not null;
        if (string.IsNullOrEmpty(text))
        {
            ClientIdStatus.IsVisible = false;
            return;
        }
        ClientIdStatus.IsVisible = true;
        ClientIdStatus.Text = id is null
            ? "That doesn't look like a Client ID - it is 32 letters and numbers. Use the copy button beside Client ID on your app's page."
            : fromClipboard ? "Client ID found on your clipboard." : "Client ID looks right.";
    }

    private async void SignIn_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || SpotifyNowPlayingService.NormaliseClientId(ClientIdBox.Text) is not { } id) return;
        if (await _viewModel.SignInSpotifyAsync(id)) Close();
    }

    private void CancelSignIn_OnClick(object? sender, RoutedEventArgs e) => _viewModel?.CancelSpotifySignIn();

    private void Forget_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ForgetSpotifyOwnApp();
        ClientIdBox.Text = string.Empty;
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void Dialog_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close();
}
