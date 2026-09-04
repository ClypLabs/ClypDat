namespace ClypDat.App.ViewModels;

public sealed class AudioTrackProcessViewModel : ViewModelBase
{
    private readonly Action<AudioTrackProcessViewModel> _changed;
    private bool _isEnabled;
    private double _volumePercent;

    public AudioTrackProcessViewModel(string name, bool isEnabled, int volumePercent, Action<AudioTrackProcessViewModel> changed)
    {
        Name = name;
        _isEnabled = isEnabled;
        _volumePercent = Math.Clamp(volumePercent, 0, 150);
        _changed = changed;
    }

    public string Name { get; }
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
    private Avalonia.Media.Imaging.Bitmap? _icon;
    public Avalonia.Media.Imaging.Bitmap? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            _changed(this);
        }
    }

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 150);
            if (!SetProperty(ref _volumePercent, clamped)) return;
            OnPropertyChanged(nameof(IsVolumeDefault));
            _changed(this);
        }
    }

    // Drives the row's reset button. Half a percent rather than an exact
    // compare: the slider snaps to whole ticks, but the value also arrives
    // from a saved profile where it went through a double round-trip.
    public bool IsVolumeDefault => Math.Abs(_volumePercent - DefaultVolumePercent) < 0.5;

    public const double DefaultVolumePercent = 100;

    public void ResetVolume() => VolumePercent = DefaultVolumePercent;
}
