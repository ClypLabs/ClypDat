namespace Eve.App.ViewModels;

public sealed class TrackLaneViewModel : ViewModelBase
{
    private double _volumePercent = 100;
    private bool _showVolumePercent;

    public TrackLaneViewModel(int streamIndex, string label, string type, string color, bool canAdjustVolume)
    {
        StreamIndex = streamIndex;
        Label = label;
        Type = type;
        Color = color;
        CanAdjustVolume = canAdjustVolume;
    }

    public int StreamIndex { get; }
    public string Label { get; }
    public string Type { get; }
    public string Color { get; }
    public bool CanAdjustVolume { get; }
    public bool IsAudio => Type == "audio";
    public bool IsVideo => Type == "video";
    public string VolumeLabel => $"{VolumePercent:0}%";

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 150);
            if (!SetProperty(ref _volumePercent, clamped)) return;
            OnPropertyChanged(nameof(VolumeLabel));
        }
    }

    public bool ShowVolumePercent
    {
        get => _showVolumePercent;
        set => SetProperty(ref _showVolumePercent, value);
    }
}
