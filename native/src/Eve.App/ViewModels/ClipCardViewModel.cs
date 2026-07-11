using Avalonia.Media.Imaging;
using Avalonia.Media;
using Eve.App.Services;

namespace Eve.App.ViewModels;

public sealed class ClipCardViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _isHovered;
    private MediaFileInfo _media;
    private string _previewImagePath;
    private Bitmap? _previewImage;

    public ClipCardViewModel(MediaFileInfo media)
    {
        _media = media;
        _previewImagePath = media.ThumbnailPath;
        SetPreviewImage(_previewImagePath);
    }

    public MediaFileInfo Media => _media;
    public string Name => Media.Name;
    public string Path => Media.Path;
    public DateTimeOffset CreatedAt => Media.CreatedAt;
    public TimeSpan Duration => Media.Duration;
    public long SizeBytes => Media.SizeBytes;
    public string DateLabel => CreatedAt.ToString("MMM d, yyyy h:mm tt");

    // Relative for anything recent (matches how Medal/most clip tools show it -
    // "9 days ago" scans faster than a timestamp), falls back to an absolute date
    // once it's old enough that "X ago" stops being useful at a glance.
    public string RelativeDateLabel
    {
        get
        {
            var age = DateTimeOffset.Now - CreatedAt;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            if (age.TotalMinutes < 1) return "Just now";
            if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min{((int)age.TotalMinutes == 1 ? "" : "s")} ago";
            if (age.TotalHours < 24) return $"{(int)age.TotalHours} hour{((int)age.TotalHours == 1 ? "" : "s")} ago";
            if (age.TotalDays < 30) return $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";
            return CreatedAt.ToString("MMM d, yyyy");
        }
    }
    public string DurationLabel => Duration > TimeSpan.Zero ? Duration.ToString("m\\:ss") : "0:00";
    public string GameLabel => "VIDEO";
    public string CaptureBackendLabel => string.IsNullOrWhiteSpace(Media.CaptureBackend) ? string.Empty : $"Captured with: {Media.CaptureBackend}";
    public bool HasCaptureBackendLabel => !string.IsNullOrWhiteSpace(CaptureBackendLabel);

    public string PreviewImagePath
    {
        get => _previewImagePath;
        private set
        {
            if (!SetProperty(ref _previewImagePath, value)) return;
            SetPreviewImage(value);
        }
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            OnPropertyChanged(nameof(IsCheckVisible));
            OnPropertyChanged(nameof(SelectionBorderBrush));
            OnPropertyChanged(nameof(SelectionBorderThickness));
        }
    }

    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (!SetProperty(ref _isHovered, value)) return;
            OnPropertyChanged(nameof(IsCheckVisible));
        }
    }

    public bool IsCheckVisible => IsSelected || IsHovered;
    public IBrush SelectionBorderBrush => IsSelected ? Brush.Parse("#5864E8") : Brush.Parse("#24303A");
    public Avalonia.Thickness SelectionBorderThickness => IsSelected ? new Avalonia.Thickness(2) : new Avalonia.Thickness(0);

    public void UpdateMedia(MediaFileInfo media)
    {
        _media = media;
        PreviewImagePath = media.ThumbnailPath;
        OnPropertyChanged(nameof(Media));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(CreatedAt));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(RelativeDateLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(CaptureBackendLabel));
        OnPropertyChanged(nameof(HasCaptureBackendLabel));
    }

    private void SetPreviewImage(string path)
    {
        try
        {
            PreviewImage = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new Bitmap(path)
                : null;
        }
        catch
        {
            PreviewImage = null;
        }
    }
}
