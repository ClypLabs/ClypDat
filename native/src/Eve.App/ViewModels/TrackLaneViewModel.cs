using Avalonia;
using Avalonia.Media.Imaging;

namespace Eve.App.ViewModels;

public sealed class TrackLaneViewModel : ViewModelBase
{
    private double _volumePercent = 100;
    private double _volumeBadgeX = 46;
    private bool _showVolumePercent;
    private bool _isMuted;
    private IReadOnlyList<double> _waveformPeaks = Array.Empty<double>();
    private Bitmap? _filmstrip;

    public TrackLaneViewModel(int streamIndex, string label, string type, string color, bool canAdjustVolume, double volumePercent = 100)
    {
        StreamIndex = streamIndex;
        Label = label;
        Type = type;
        Color = color;
        CanAdjustVolume = canAdjustVolume;
        _volumePercent = Math.Clamp(volumePercent, 0, 150);
    }

    public int StreamIndex { get; }
    public string Label { get; }
    public string Type { get; }
    public string Color { get; }
    public bool CanAdjustVolume { get; }
    public bool IsAudio => Type == "audio";
    public bool IsVideo => Type == "video";
    // Video bumped from its old 32 (a plain outlined box, no real content)
    // now that it renders filmstrip thumbnails (TimelineLaneControl) - taller
    // than that so the frames are readable, but not as tall as the audio
    // lanes either (64 read as too dominant next to them). MainWindow.axaml's
    // TrimSelection/TrimStartHandle/TrimEndHandle heights are hardcoded to
    // match this - keep them in sync if this changes.
    public double LaneHeight => IsVideo ? 42 : 56;
    public string VolumeLabel => $"{VolumePercent:0}%";
    public Thickness VolumeBadgeMargin => new(VolumeBadgeX, -8, 0, 0);
    public string HeaderClass => IsAudio ? "audioHeader" : "videoHeader";

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 150);
            if (!SetProperty(ref _volumePercent, clamped)) return;
            OnPropertyChanged(nameof(VolumeLabel));
            OnPropertyChanged(nameof(VolumeBadgeMargin));
            OnPropertyChanged(nameof(EffectiveVolumePercent));
            OnPropertyChanged(nameof(IsVolumeNonDefault));
        }
    }

    // Drives the per-track reset button (MainWindow.axaml) - only shown once
    // a track has actually been moved off its 100% default, so the row
    // doesn't show a reset affordance for every track all the time.
    public bool IsVolumeNonDefault => Math.Abs(VolumePercent - 100) > 0.01;

    public bool ShowVolumePercent
    {
        get => _showVolumePercent;
        set => SetProperty(ref _showVolumePercent, value);
    }

    // Independent of VolumePercent so un-muting restores whatever level was set
    // before, instead of the mute action itself forgetting the prior value.
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (!SetProperty(ref _isMuted, value)) return;
            OnPropertyChanged(nameof(EffectiveVolumePercent));
        }
    }

    // What the volume filter/preview should actually use - 0 while muted, the
    // real percent otherwise.
    public double EffectiveVolumePercent => IsMuted ? 0 : VolumePercent;

    public double VolumeBadgeX
    {
        get => _volumeBadgeX;
        set
        {
            if (!SetProperty(ref _volumeBadgeX, value)) return;
            OnPropertyChanged(nameof(VolumeBadgeMargin));
        }
    }

    public IReadOnlyList<double> WaveformPeaks
    {
        get => _waveformPeaks;
        set => SetProperty(ref _waveformPeaks, value);
    }

    // Only meaningful for the video lane - a single spritesheet image
    // (MediaProbeService.FilmstripFrameCount frames tiled left-to-right)
    // TimelineLaneControl slices and draws across the timeline (see
    // EnsureFilmstripAsync in MediaProbeService for how this gets
    // generated/cached).
    public Bitmap? Filmstrip
    {
        get => _filmstrip;
        set => SetProperty(ref _filmstrip, value);
    }
}
