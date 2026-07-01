using Avalonia.Threading;
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

    public ClipCardViewModel(MediaFileInfo media, MediaProbeService mediaProbe)
    {
        Media = media;
        _mediaProbe = mediaProbe;
        _previewImagePath = media.ThumbnailPath;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _previewTimer.Tick += (_, _) => AdvancePreview();
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
        private set => SetProperty(ref _previewImagePath, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            OnPropertyChanged(nameof(IsCheckVisible));
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
}
