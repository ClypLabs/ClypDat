using Avalonia.Media.Imaging;
using ClypDat.App.Services;
using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

// One game in the Custom Game Settings tab strip. Owns the bound state for its
// tab AND for the settings panel shown when it is selected, so switching tabs
// is a selection change rather than a rebuild of every control.
public sealed class CustomGameTabViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Action _save;
    private bool _isSelected;

    public CustomGameTabViewModel(string detectionKey, CustomGameProfile profile, AppSettings settings, Action save)
    {
        DetectionKey = detectionKey;
        Profile = profile;
        _settings = settings;
        _save = save;
        Icon = GameIconService.TryLoad(profile.DisplayName);
    }

    // Same identity GameCaptureOverrides uses - see CustomGameSettingsResolver.
    public string DetectionKey { get; }
    public CustomGameProfile Profile { get; }
    public string DisplayName => Profile.DisplayName;

    // Null for a game with no cached icon; the tab falls back to its initial.
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // --- group toggles ----------------------------------------------------
    // Switching a group ON seeds it from the CURRENT global values, so the
    // override starts as an exact copy of what this game already recorded
    // with. Switching it off leaves the values in place, so switching it back
    // on returns the user to what they had rather than to the global again.

    public bool HasRecordingMode
    {
        get => Has(CustomGameSettingsResolver.RecordingModeGroup);
        set => SetGroup(CustomGameSettingsResolver.RecordingModeGroup, value);
    }

    public bool HasQuality
    {
        get => Has(CustomGameSettingsResolver.QualityGroup);
        set => SetGroup(CustomGameSettingsResolver.QualityGroup, value);
    }

    public bool HasReplay
    {
        get => Has(CustomGameSettingsResolver.ReplayGroup);
        set => SetGroup(CustomGameSettingsResolver.ReplayGroup, value);
    }

    public bool HasAudio
    {
        get => Has(CustomGameSettingsResolver.AudioGroup);
        set => SetGroup(CustomGameSettingsResolver.AudioGroup, value);
    }

    public bool HasFullSession
    {
        get => Has(CustomGameSettingsResolver.FullSessionGroup);
        set => SetGroup(CustomGameSettingsResolver.FullSessionGroup, value);
    }

    public bool HasAnyGroup => Profile.Groups.Count > 0;

    private bool Has(string group) => CustomGameSettingsResolver.HasGroup(Profile, group);

    private void SetGroup(string group, bool enabled)
    {
        if (Has(group) == enabled) return;
        if (enabled)
        {
            CustomGameSettingsResolver.SeedGroupFromGlobal(_settings, Profile, group);
            Profile.Groups.Add(group);
        }
        else
        {
            Profile.Groups.RemoveAll(existing => string.Equals(existing, group, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(HasRecordingMode));
        OnPropertyChanged(nameof(HasQuality));
        OnPropertyChanged(nameof(HasReplay));
        OnPropertyChanged(nameof(HasAudio));
        OnPropertyChanged(nameof(HasFullSession));
        OnPropertyChanged(nameof(HasAnyGroup));
        RaiseAllValues();
        _save();
    }

    // --- recording mode ---------------------------------------------------
    // Three radios over one stored string. Bound as booleans because a
    // RadioButton's IsChecked is a bool, and the setter ignores the deselect
    // half of the pair: the group already guarantees exactly one is on, and
    // acting on false would clear the mode as the new one arrives.

    public bool IsManualCapture
    {
        get => IsMode(CustomGameSettingsResolver.ManualMode);
        set { if (value) SetMode(CustomGameSettingsResolver.ManualMode); }
    }

    public bool IsFullSessionCapture
    {
        get => IsMode(CustomGameSettingsResolver.FullSessionMode);
        set { if (value) SetMode(CustomGameSettingsResolver.FullSessionMode); }
    }

    public bool IsRecordingOff
    {
        get => IsMode(CustomGameSettingsResolver.OffMode);
        set { if (value) SetMode(CustomGameSettingsResolver.OffMode); }
    }

    private bool IsMode(string mode) => string.Equals(Profile.RecordingMode, mode, StringComparison.OrdinalIgnoreCase);

    private void SetMode(string mode)
    {
        if (IsMode(mode)) return;
        Profile.RecordingMode = mode;
        OnPropertyChanged(nameof(IsManualCapture));
        OnPropertyChanged(nameof(IsFullSessionCapture));
        OnPropertyChanged(nameof(IsRecordingOff));
        _save();
    }

    // --- quality ----------------------------------------------------------

    public string ReplayVideoCodec
    {
        get => Profile.ReplayVideoCodec;
        set => Set(v => Profile.ReplayVideoCodec = v, Profile.ReplayVideoCodec, value);
    }

    public string ReplayEncoderMode
    {
        get => Profile.ReplayEncoderMode;
        set => Set(v => Profile.ReplayEncoderMode = v, Profile.ReplayEncoderMode, value);
    }

    // Combo-bound, so int rather than the double a Slider would need. The
    // Quality group uses the same preset cards and dropdowns the global
    // Recording Quality card does - a per-game override that looked nothing
    // like the setting it overrides is harder to read, not easier.
    public int ReplayBitrateMbps
    {
        get => Profile.ReplayBitrateMbps;
        set => Set(v => Profile.ReplayBitrateMbps = v, Profile.ReplayBitrateMbps, value);
    }

    public int ReplayFrameRate
    {
        get => Profile.ReplayFrameRate;
        set => Set(v => Profile.ReplayFrameRate = v, Profile.ReplayFrameRate, value);
    }

    public int ReplayMaxHeight
    {
        get => Profile.ReplayMaxHeight;
        set => Set(v => Profile.ReplayMaxHeight = v, Profile.ReplayMaxHeight, value);
    }

    // "20M" in the dropdown, 20 in the profile. Same option list the global
    // card uses, so the two cannot drift apart.
    public string SelectedBitrateOption
    {
        get => $"{Profile.ReplayBitrateMbps}M";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!int.TryParse(value.TrimEnd('M', 'm'), out var mbps)) return;
            if (Profile.ReplayBitrateMbps == mbps) return;
            Profile.ReplayBitrateMbps = mbps;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReplayBitrateMbps));
            RaiseQualityPreset();
            _save();
        }
    }

    // Null while the values match no preset, which is exactly what the Custom
    // card means - the list's last entry is Custom and is selected then.
    private MainWindowViewModel.ReplayQualityPreset? _selectedQualityPreset;

    public MainWindowViewModel.ReplayQualityPreset? SelectedQualityPreset
    {
        get => _selectedQualityPreset ??= MatchPreset();
        set
        {
            if (value is null || ReferenceEquals(_selectedQualityPreset, value)) return;
            _selectedQualityPreset = value;
            if (!value.IsCustom)
            {
                Profile.ReplayMaxHeight = value.Height;
                Profile.ReplayFrameRate = value.FrameRate;
                Profile.ReplayBitrateMbps = value.Bitrate;
                OnPropertyChanged(nameof(ReplayMaxHeight));
                OnPropertyChanged(nameof(ReplayFrameRate));
                OnPropertyChanged(nameof(ReplayBitrateMbps));
                OnPropertyChanged(nameof(SelectedBitrateOption));
                _save();
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomQuality));
        }
    }

    public bool IsCustomQuality => SelectedQualityPreset?.IsCustom ?? true;

    private MainWindowViewModel.ReplayQualityPreset? MatchPreset()
    {
        var presets = MainWindowViewModel.QualityPresets;
        return presets.FirstOrDefault(preset => preset.Matches(Profile.ReplayMaxHeight, Profile.ReplayFrameRate, Profile.ReplayBitrateMbps))
               ?? presets[^1];
    }

    // Changing one of the custom dropdowns can land back exactly on a preset,
    // and the cards have to follow rather than stay on Custom.
    private void RaiseQualityPreset()
    {
        _selectedQualityPreset = MatchPreset();
        OnPropertyChanged(nameof(SelectedQualityPreset));
        OnPropertyChanged(nameof(IsCustomQuality));
    }

    public string ReplayFrameRateMode
    {
        get => Profile.ReplayFrameRateMode;
        set => Set(v => Profile.ReplayFrameRateMode = v, Profile.ReplayFrameRateMode, value);
    }

    // --- replay length and hotkey ----------------------------------------

    public double ReplayDurationSeconds
    {
        get => Profile.ReplayDurationSeconds;
        set => Set(v => Profile.ReplayDurationSeconds = (int)Math.Round(v), Profile.ReplayDurationSeconds, value);
    }

    public string SaveReplayHotkey
    {
        get => Profile.SaveReplayHotkey;
        set => Set(v => Profile.SaveReplayHotkey = v, Profile.SaveReplayHotkey, value);
    }

    // --- audio ------------------------------------------------------------

    public double GameAudioVolumePercent
    {
        get => Profile.GameAudioVolumePercent;
        set => Set(v => Profile.GameAudioVolumePercent = (int)Math.Round(Math.Clamp(v, 0, 150)), Profile.GameAudioVolumePercent, value);
    }

    public double MicrophoneVolumePercent
    {
        get => Profile.MicrophoneVolumePercent;
        set => Set(v => Profile.MicrophoneVolumePercent = (int)Math.Round(Math.Clamp(v, 0, 150)), Profile.MicrophoneVolumePercent, value);
    }

    public bool MicrophoneNoiseSuppressionEnabled
    {
        get => Profile.MicrophoneNoiseSuppressionEnabled;
        set => Set(v => Profile.MicrophoneNoiseSuppressionEnabled = v, Profile.MicrophoneNoiseSuppressionEnabled, value);
    }

    public double MicrophoneNoiseGateThresholdDb
    {
        get => Profile.MicrophoneNoiseGateThresholdDb;
        set => Set(v => Profile.MicrophoneNoiseGateThresholdDb = MicrophoneNoiseSuppression.ClampGateThresholdDb(v),
            Profile.MicrophoneNoiseGateThresholdDb, value);
    }

    // --- full session -----------------------------------------------------

    public bool FullSessionRecordingEnabled
    {
        get => Profile.FullSessionRecordingEnabled;
        set => Set(v => Profile.FullSessionRecordingEnabled = v, Profile.FullSessionRecordingEnabled, value);
    }

    public string FullSessionVideoCodec
    {
        get => Profile.FullSessionVideoCodec;
        set => Set(v => Profile.FullSessionVideoCodec = v, Profile.FullSessionVideoCodec, value);
    }

    public double FullSessionQuotaGb
    {
        get => Profile.FullSessionQuotaGb;
        set => Set(v => Profile.FullSessionQuotaGb = (int)Math.Round(Math.Max(0, v)), Profile.FullSessionQuotaGb, value);
    }

    // Assigns through a setter delegate so every property above is one line and
    // none of them can forget to persist. Compares before writing because
    // two-way slider bindings re-assign their own value constantly.
    private void Set<T>(Action<T> assign, T current, T value, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(property);
        if (property is nameof(ReplayMaxHeight) or nameof(ReplayFrameRate) or nameof(ReplayBitrateMbps))
        {
            OnPropertyChanged(nameof(SelectedBitrateOption));
            RaiseQualityPreset();
        }

        _save();
    }

    private void RaiseAllValues()
    {
        OnPropertyChanged(nameof(IsManualCapture));
        OnPropertyChanged(nameof(IsFullSessionCapture));
        OnPropertyChanged(nameof(IsRecordingOff));
        OnPropertyChanged(nameof(ReplayVideoCodec));
        OnPropertyChanged(nameof(ReplayEncoderMode));
        OnPropertyChanged(nameof(ReplayBitrateMbps));
        OnPropertyChanged(nameof(ReplayFrameRate));
        OnPropertyChanged(nameof(ReplayMaxHeight));
        OnPropertyChanged(nameof(SelectedBitrateOption));
        OnPropertyChanged(nameof(SelectedQualityPreset));
        OnPropertyChanged(nameof(IsCustomQuality));
        OnPropertyChanged(nameof(ReplayFrameRateMode));
        OnPropertyChanged(nameof(ReplayDurationSeconds));
        OnPropertyChanged(nameof(SaveReplayHotkey));
        OnPropertyChanged(nameof(GameAudioVolumePercent));
        OnPropertyChanged(nameof(MicrophoneVolumePercent));
        OnPropertyChanged(nameof(MicrophoneNoiseSuppressionEnabled));
        OnPropertyChanged(nameof(MicrophoneNoiseGateThresholdDb));
        OnPropertyChanged(nameof(FullSessionRecordingEnabled));
        OnPropertyChanged(nameof(FullSessionVideoCodec));
        OnPropertyChanged(nameof(FullSessionQuotaGb));
    }
}
