using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ClypDat.App.ViewModels;

public sealed class TrackLaneViewModel : ViewModelBase
{
    public const double StandardAudioLaneHeight = 66;
    public const double CompactAudioLaneHeight = 48;
    public const double CompactAudioLaneHeightReduction = StandardAudioLaneHeight - CompactAudioLaneHeight;

    private double _volumePercent = 100;
    private double _volumeBadgeX = 46;
    private bool _showVolumePercent;
    private bool _isMuted;
    private bool _isLastAudioTrack;
    private IReadOnlyList<double> _waveformPeaks = Array.Empty<double>();
    private Bitmap? _filmstrip;
    private readonly Color _laneColor;
    private readonly SolidColorBrush _volumeAccentBrush;

    public TrackLaneViewModel(
        int streamIndex,
        string label,
        string type,
        string color,
        bool canAdjustVolume,
        double volumePercent = 100,
        bool isCompactAudioLane = false)
    {
        StreamIndex = streamIndex;
        Label = label;
        Type = type;
        Color = color;
        _laneColor = Avalonia.Media.Color.Parse(color);
        VolumeBrush = new SolidColorBrush(_laneColor);
        CanAdjustVolume = canAdjustVolume;
        IsCompactAudioLane = isCompactAudioLane;
        _volumePercent = Math.Clamp(volumePercent, 0, 150);
        _volumeAccentBrush = new SolidColorBrush(GetVolumeAccentColor(_volumePercent));
    }

    public int StreamIndex { get; }
    public string Label { get; }
    public string Type { get; }
    public string Color { get; }
    public IBrush VolumeBrush { get; }
    public IBrush VolumeAccentBrush => _volumeAccentBrush;
    public bool CanAdjustVolume { get; }
    public bool IsAudio => Type == "audio";
    public bool IsVideo => Type == "video";
    public bool IsCompactAudioLane { get; }
    // Video bumped from its old 32 (a plain outlined box, no real content)
    // now that it renders filmstrip thumbnails (TimelineLaneControl) - taller
    // than that so the frames are readable, but not as tall as the audio
    // lanes either (64 read as too dominant next to them).
    // Audio went 56 -> 66 so the label row and the slider row each get their
    // own space instead of the slider being pulled up under the label with a
    // negative margin - that overlap is what made the box read as cramped, and
    // it was also what let the slider's hit area cover the Reset button. The
    // extra headroom is also what lets the Slider take its natural height:
    // squeezed into less, its 16px thumb overflowed the control and was
    // clipped along the bottom edge.
    // More than three audio tracks would otherwise make the timeline consume too
    // much of a shorter editor window. Compact lanes still leave a full label,
    // mute control, and 16px slider thumb, while saving 18px per audio lane.
    public double LaneHeight => IsVideo ? 44 : IsCompactAudioLane ? CompactAudioLaneHeight : StandardAudioLaneHeight;
    // Keep the normal 6px separator between every lane, but do not leave an
    // empty strip below the final audio (normally microphone) lane.
    public Thickness LaneMargin => IsAudio && IsLastAudioTrack ? new Thickness(0) : new Thickness(0, 0, 0, 6);
    // Audio labels sit a couple of pixels low, optically centring them in the
    // space above the slider row. A video label is centred in the whole box
    // (LabelRowSpan) and needs no nudge.
    public Thickness LaneContentMargin => IsCompactAudioLane
        ? new Thickness(12, 4)
        : new Thickness(12, 7);
    public Thickness VolumeSliderMargin => IsCompactAudioLane
        ? new Thickness(0, -6, 0, 0)
        : new Thickness(0, -2, 0, 0);
    public Thickness LabelMargin => IsAudio
        ? new Thickness(0, IsCompactAudioLane ? 0 : 2, 0, 0)
        : new Thickness(0);
    // A video lane has no slider row under its label, so its header spans the
    // whole box and centres in it instead of sitting at the top with empty
    // space below - which is what made "Video" look top-aligned next to the
    // audio lanes, whose labels genuinely do sit above their sliders.
    public int LabelRowSpan => IsVideo ? 2 : 1;
    public string VolumeLabel => $"{VolumePercent:0}%";
    public Thickness VolumeBadgeMargin => new(VolumeBadgeX, -8, 0, 0);
    public string HeaderClass => IsAudio ? "audioHeader" : "videoHeader";

    public bool IsLastAudioTrack
    {
        get => _isLastAudioTrack;
        set
        {
            if (SetProperty(ref _isLastAudioTrack, value)) OnPropertyChanged(nameof(LaneMargin));
        }
    }

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
            _volumeAccentBrush.Color = GetVolumeAccentColor(clamped);
            OnPropertyChanged(nameof(VolumeAccentBrush));
        }
    }

    private Color GetVolumeAccentColor(double volumePercent) => volumePercent switch
    {
        > 125 => Avalonia.Media.Color.Parse("#F05A63"),
        > 100 => Avalonia.Media.Color.Parse("#F4B73E"),
        _ => _laneColor
    };

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
