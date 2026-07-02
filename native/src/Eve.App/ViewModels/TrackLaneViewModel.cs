using Avalonia;

namespace Eve.App.ViewModels;

public sealed class TrackLaneViewModel : ViewModelBase
{
    private double _volumePercent = 100;
    private double _volumeBadgeX = 46;
    private bool _showVolumePercent;
    private IReadOnlyList<double> _waveformPeaks = Array.Empty<double>();

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
    public double LaneHeight => IsVideo ? 32 : 56;
    public string VolumeLabel => $"{VolumePercent:0}%";
    public Thickness VolumeBadgeMargin => new(Math.Clamp(VolumeBadgeX - 18, -2, 116), -26, 0, 0);
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
        }
    }

    public bool ShowVolumePercent
    {
        get => _showVolumePercent;
        set => SetProperty(ref _showVolumePercent, value);
    }

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
}
