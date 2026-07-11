using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Eve.App.Services;

namespace Eve.App.ViewModels;

public sealed class ClipCardViewModel : ViewModelBase
{
    private readonly MediaProbeService _mediaProbe;
    private readonly DispatcherTimer _previewTimer;
    private CancellationTokenSource? _previewCts;
    private Task<IReadOnlyList<string>>? _frameExtractionTask;
    private IReadOnlyList<string> _previewFrames = Array.Empty<string>();
    private IReadOnlyList<Bitmap> _previewBitmaps = Array.Empty<Bitmap>();
    private int _previewIndex;
    private bool _isSelected;
    private bool _isHovered;
    private MediaFileInfo _media;
    private string _previewImagePath;
    private Bitmap? _previewImage;

    public ClipCardViewModel(MediaFileInfo media, MediaProbeService mediaProbe)
    {
        _media = media;
        _mediaProbe = mediaProbe;
        _previewImagePath = media.ThumbnailPath;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _previewTimer.Tick += (_, _) => AdvancePreview();
        SetPreviewImage(_previewImagePath);
    }

    public MediaFileInfo Media => _media;
    public string Name => Media.Name;
    public string Path => Media.Path;
    public DateTimeOffset CreatedAt => Media.CreatedAt;
    public TimeSpan Duration => Media.Duration;
    public long SizeBytes => Media.SizeBytes;
    public string DateLabel => CreatedAt.ToString("MMM d, yyyy h:mm tt");
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
        _previewFrames = Array.Empty<string>();
        _frameExtractionTask = null;
        foreach (var bitmap in _previewBitmaps) bitmap.Dispose();
        _previewBitmaps = Array.Empty<Bitmap>();
        PreviewImagePath = media.ThumbnailPath;
        OnPropertyChanged(nameof(Media));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(CreatedAt));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(CaptureBackendLabel));
        OnPropertyChanged(nameof(HasCaptureBackendLabel));
    }

    public async Task StartPreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        try
        {
            if (_previewBitmaps.Count == 0)
            {
                // ffmpeg extraction is NOT tied to the hover token - a quick hover
                // that moves on before extraction finishes used to cancel and kill
                // it mid-run, leaving a permanently-partial frame set on disk that
                // could never pass EnsurePreviewFramesAsync's "is the cache
                // complete" check, so every future hover of that same clip
                // restarted from zero and got cancelled again: it could never
                // finish under normal browsing. Now extraction always runs to
                // completion once started (cached to disk for next time); only
                // waiting for it and displaying frames gets cancelled on exit.
                _frameExtractionTask ??= _mediaProbe.EnsurePreviewFramesAsync(Media, CancellationToken.None);
                _previewFrames = await _frameExtractionTask.WaitAsync(token);

                var loaded = await Task.Run(() => LoadPreviewBitmaps(_previewFrames, token), token);
                if (token.IsCancellationRequested)
                {
                    // This call was superseded before it could show anything -
                    // whatever it just decoded is otherwise unreachable and would
                    // never get disposed, which is exactly what was leaking memory
                    // on every quick hover-then-move-on.
                    foreach (var bitmap in loaded) bitmap.Dispose();
                    return;
                }

                _previewBitmaps = loaded;
            }

            if (_previewBitmaps.Count == 0) return;
            _previewIndex = 0;
            _previewTimer.Interval = GetPreviewFrameInterval();
            PreviewImage = _previewBitmaps[_previewIndex];
            _previewTimer.Start();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void StopPreview()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        _previewTimer.Stop();
        PreviewImagePath = Media.ThumbnailPath;

        // The frame set stays cached on disk (ffmpeg won't re-extract it), but
        // freeing the decoded Bitmaps here keeps memory bounded to whichever
        // clip is actively being hovered right now instead of accumulating one
        // full frame set per clip ever hovered in the session.
        foreach (var bitmap in _previewBitmaps) bitmap.Dispose();
        _previewBitmaps = Array.Empty<Bitmap>();
        _previewFrames = Array.Empty<string>();
    }

    private void AdvancePreview()
    {
        if (_previewBitmaps.Count == 0) return;
        _previewIndex = (_previewIndex + 1) % _previewBitmaps.Count;
        PreviewImage = _previewBitmaps[_previewIndex];
    }

    private static IReadOnlyList<Bitmap> LoadPreviewBitmaps(IReadOnlyList<string> framePaths, CancellationToken token)
    {
        var bitmaps = new List<Bitmap>(framePaths.Count);
        foreach (var path in framePaths)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                if (File.Exists(path)) bitmaps.Add(new Bitmap(path));
            }
            catch
            {
                // Skip corrupt preview frames.
            }
        }

        return bitmaps;
    }

    private TimeSpan GetPreviewFrameInterval()
    {
        if (Duration <= TimeSpan.Zero || _previewBitmaps.Count == 0)
        {
            return TimeSpan.FromMilliseconds(250);
        }

        var frameMs = Duration.TotalMilliseconds / _previewBitmaps.Count;
        return TimeSpan.FromMilliseconds(Math.Max(17, frameMs));
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
