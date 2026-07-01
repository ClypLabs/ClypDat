using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Eve.App.Services;

namespace Eve.App.ViewModels;

public sealed class ClipCardViewModel : ViewModelBase
{
    private readonly MediaProbeService _mediaProbe;
    private readonly DispatcherTimer _previewTimer;
    private IReadOnlyList<string> _previewFrames = Array.Empty<string>();
    private int _previewIndex;
    private bool _isSelected;
    private bool _isHovered;
    private string _previewImagePath;
    private Bitmap? _previewImage;

    public ClipCardViewModel(MediaFileInfo media, MediaProbeService mediaProbe)
    {
        Media = media;
        _mediaProbe = mediaProbe;
        _previewImagePath = media.ThumbnailPath;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _previewTimer.Tick += (_, _) => AdvancePreview();
        SetPreviewImage(_previewImagePath);
    }

    public MediaFileInfo Media { get; }
    public string Name => Media.Name;
    public string Path => Media.Path;
    public DateTimeOffset CreatedAt => Media.CreatedAt;
    public TimeSpan Duration => Media.Duration;
    public long SizeBytes => Media.SizeBytes;
    public string DateLabel => CreatedAt.ToString("MMM d, yyyy");
    public string DurationLabel => Duration > TimeSpan.Zero ? Duration.ToString("m\\:ss") : "0:00";
    public string GameLabel => "VIDEO";

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
    public IBrush SelectionBorderBrush => IsSelected ? Brush.Parse("#22CFC3") : Brush.Parse("#24303A");
    public Avalonia.Thickness SelectionBorderThickness => IsSelected ? new Avalonia.Thickness(2) : new Avalonia.Thickness(0);

    public async Task StartPreviewAsync()
    {
        if (_previewFrames.Count == 0)
        {
            _previewFrames = await _mediaProbe.EnsurePreviewFramesAsync(Media);
        }

        if (_previewFrames.Count == 0) return;
        _previewIndex = 0;
        PreviewImagePath = _previewFrames[_previewIndex];
        _previewTimer.Start();
    }

    public void StopPreview()
    {
        _previewTimer.Stop();
        PreviewImagePath = Media.ThumbnailPath;
    }

    private void AdvancePreview()
    {
        if (_previewFrames.Count == 0) return;
        _previewIndex = (_previewIndex + 1) % _previewFrames.Count;
        PreviewImagePath = _previewFrames[_previewIndex];
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
