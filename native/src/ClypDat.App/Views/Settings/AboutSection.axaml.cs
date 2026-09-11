using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class AboutSection : UserControl
{
    private static readonly HttpClient AvatarClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Avatar[] _avatars;
    private CancellationTokenSource? _avatarDownloads;
    private bool _isAttached;
    private int _avatarOpening;

    // The logo is a toggle between the current mark and the original hexagon one.
    // Release rather than press, so a press that drags off the logo changes nothing.
    private void Logo_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || sender is not Visual logo) return;
        var point = e.GetPosition(logo);
        if (point.X < 0 || point.Y < 0 || point.X > logo.Bounds.Width || point.Y > logo.Bounds.Height) return;
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel model) return;
        model.Settings.UseClassicLogo = !model.Settings.UseClassicLogo;
        model.SaveSettings();
        (Application.Current as App)?.ApplyLogoStyle(model.Settings.UseClassicLogo);
        e.Handled = true;
    }

    public AboutSection()
    {
        InitializeComponent();
        _avatars =
        [
            new("https://github.com/Stormanzanii.png", ArashiiAvatar, ArashiiInitials),
            new("https://github.com/iPixelGalaxy.png", IPixelGalaxyAvatar, IPixelGalaxyInitials),
            new("https://github.com/Spikerko.png", SpikerkoAvatar, SpikerkoInitials)
        ];
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        if (IsVisible)
        {
            _avatarOpening++;
            StartAvatarDownloads();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _avatarDownloads?.Cancel();
        _avatarDownloads?.Dispose();
        _avatarDownloads = null;

        foreach (var avatar in _avatars)
        {
            avatar.Image.Source = null;
            avatar.Initials.IsVisible = true;
            avatar.Bitmap?.Dispose();
            avatar.Bitmap = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || !IsVisible || !_isAttached) return;

        _avatarOpening++;
        StartAvatarDownloads();
    }

    private void StartAvatarDownloads()
    {
        if (!_isAttached || !IsVisible) return;

        var pending = _avatars
            .Where(avatar => avatar.Bitmap is null && !avatar.IsLoading && avatar.LastAttemptOpening != _avatarOpening)
            .ToArray();
        if (pending.Length == 0) return;

        _avatarDownloads ??= new CancellationTokenSource();
        foreach (var avatar in pending)
        {
            avatar.IsLoading = true;
            avatar.LastAttemptOpening = _avatarOpening;
            _ = LoadAvatarAsync(avatar, _avatarDownloads.Token);
        }
    }

    private async Task LoadAvatarAsync(Avatar avatar, CancellationToken cancellationToken)
    {
        Bitmap? bitmap = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await AvatarClient.GetAsync(avatar.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            bitmap = await Task.Run(() => LoadBitmap(bytes), timeout.Token).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || !_isAttached) return;

                avatar.Image.Source = bitmap;
                avatar.Initials.IsVisible = false;
                avatar.Bitmap = bitmap;
                bitmap = null;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The About control left the visual tree.
        }
        catch
        {
            // Keep initials visible. Failed avatars retry next time About opens.
        }
        finally
        {
            bitmap?.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                avatar.IsLoading = false;
                if (_isAttached && IsVisible && avatar.LastAttemptOpening != _avatarOpening)
                    StartAvatarDownloads();
            });
        }
    }

    private static Bitmap LoadBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }

    private sealed class Avatar(string url, Image image, TextBlock initials)
    {
        public string Url { get; } = url;
        public Image Image { get; } = image;
        public TextBlock Initials { get; } = initials;
        public Bitmap? Bitmap { get; set; }
        public bool IsLoading { get; set; }
        public int LastAttemptOpening { get; set; } = -1;
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void LicenseLinkText_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        => Owner?.LicenseLinkText_OnPointerPressed(sender, e);

    private void CreditCard_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Opening a browser is best-effort; failing it must not disrupt Settings.
        }
    }

    private async void CheckUpdatesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner || !CheckNowButton.IsEnabled) return;

        CheckNowButton.IsEnabled = false;
        CheckNowButtonText.IsVisible = false;
        CheckingContent.IsVisible = true;
        try
        {
            await owner.CheckUpdatesAsync();
        }
        finally
        {
            CheckingContent.IsVisible = false;
            CheckNowButtonText.IsVisible = true;
            CheckNowButton.IsEnabled = true;
        }
    }

    private void OpenGitHubButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.OpenGitHubButton_OnClick(sender, e);

    private void ExportCaptureDiagnosticsButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ExportCaptureDiagnosticsButton_OnClick(sender, e);

    private void OpenLogsButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.OpenLogsButton_OnClick(sender, e);

    private void ShowWalkthroughButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ShowWalkthroughButton_OnClick(sender, e);

    private void OpenLicensesButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.OpenLicensesButton_OnClick(sender, e);
}
