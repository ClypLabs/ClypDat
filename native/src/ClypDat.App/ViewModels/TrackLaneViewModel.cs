using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ClypDat.App.ViewModels;

public sealed class TrackLaneViewModel : ViewModelBase
{
    private double _volumePercent = 100;
    private double _volumeBadgeX = 46;
    private bool _showVolumePercent;
    private bool _isMuted;
    private bool _isLastAudioTrack;
    private IReadOnlyList<double> _waveformPeaks = Array.Empty<double>();
    private Bitmap? _filmstrip;

    public TrackLaneViewModel(int streamIndex, string label, string type, string color, bool canAdjustVolume, double volumePercent = 100)
    {
        StreamIndex = streamIndex;
        Label = label;
        Type = type;
        Color = color;
        VolumeBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(color));
        CanAdjustVolume = canAdjustVolume;
        _volumePercent = Math.Clamp(volumePercent, 0, 150);
    }

    public int StreamIndex { get; }
    public string Label { get; }
    public string Type { get; }
    public string Color { get; }
    public IBrush VolumeBrush { get; }
    public bool CanAdjustVolume { get; }
    public bool IsAudio => Type == "audio";
    public bool IsVideo => Type == "video";
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
    public double LaneHeight => IsVideo ? 44 : 66;
    // Keep the normal 6px separator between every lane, but do not leave an
    // empty strip below the final audio (normally microphone) lane.
    public Thickness LaneMargin => IsAudio && IsLastAudioTrack ? new Thickness(0) : new Thickness(0, 0, 0, 6);
    // Audio labels sit a couple of pixels low, optically centring them in the
    // space above the slider row. A video label is centred in the whole box
    // (LabelRowSpan) and needs no nudge.
    public Thickness LabelMargin => IsAudio ? new Thickness(0, 2, 0, 0) : new Thickness(0);
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
