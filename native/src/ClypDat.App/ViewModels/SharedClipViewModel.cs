using System.Globalization;
using Avalonia.Media.Imaging;
using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

/// <summary>One card on the Shared clips page.</summary>
public sealed class SharedClipViewModel : ViewModelBase
{
    private static readonly HttpClient ThumbnailHttp = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 4 * 1024 * 1024 };

    internal SharedClipViewModel(HostedClip clip)
    {
        Id = clip.Id;
        Url = clip.Url;
        Bytes = clip.Bytes;
        ThumbnailUrl = clip.ThumbnailUrl;
        Title = string.IsNullOrWhiteSpace(clip.Title) ? "Untitled clip" : clip.Title;
        // The length sits on the thumbnail, as on library tiles; the line under
        // the title is when it was shared and how much storage it takes.
        DurationLabel = clip.DurationMs is > 0 and var ms ? ClipDurationFormatter.Format(TimeSpan.FromMilliseconds(ms)) : null;
        Details = $"{clip.CreatedAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)} · {MainWindowViewModel.FormatStorage(clip.Bytes)}";
    }

    public string Id { get; }
    public string Url { get; }
    public long Bytes { get; }
    public string Title { get; }
    public string Details { get; }
    public string? DurationLabel { get; }
    public bool HasDuration => DurationLabel is not null;
    internal string? ThumbnailUrl { get; }

    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set { if (SetProperty(ref _thumbnail, value)) OnPropertyChanged(nameof(HasThumbnail)); }
    }
    public bool HasThumbnail => Thumbnail is not null;

    private bool _confirmingDelete;
    // Delete asks once: the first press turns the button into "Delete for good".
    public bool ConfirmingDelete
    {
        get => _confirmingDelete;
        set { if (SetProperty(ref _confirmingDelete, value)) OnPropertyChanged(nameof(DeleteLabel)); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(DeleteLabel)); }
    }

    public string DeleteLabel => IsBusy ? "Deleting…" : ConfirmingDelete ? "Click again to delete for good" : "Delete";

    private bool _copied;
    public bool Copied
    {
        get => _copied;
        set { if (SetProperty(ref _copied, value)) OnPropertyChanged(nameof(CopyLabel)); }
    }
    public string CopyLabel => Copied ? "Copied" : "Copy link";

    /// <summary>
    /// The thumbnail the site made public with the clip. Only ever fetched over
    /// HTTPS; a missing or broken one leaves the card on its placeholder.
    /// </summary>
    internal async Task LoadThumbnailAsync(CancellationToken cancellationToken)
    {
        if (Thumbnail is not null || ThumbnailUrl is null) return;
        if (!Uri.TryCreate(ThumbnailUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            var bytes = await ThumbnailHttp.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, 480, BitmapInterpolationMode.MediumQuality);
            }, cancellationToken).ConfigureAwait(false);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Thumbnail = bitmap);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Debug($"Shared clips: thumbnail for {Id} unavailable ({error.Message}).");
        }
    }
}
