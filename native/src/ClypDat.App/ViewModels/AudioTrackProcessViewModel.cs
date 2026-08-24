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
            _changed(this);
        }
    }
}
