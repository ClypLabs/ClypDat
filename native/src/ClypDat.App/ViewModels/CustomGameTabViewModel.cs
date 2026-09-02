using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
    private readonly Action<CustomGameSettingChange>? _settingChanged;
    private bool _isSelected;
    private bool _qualityWarningAcknowledged;

    public CustomGameTabViewModel(string detectionKey, CustomGameProfile profile, AppSettings settings, Action save,
        Action<CustomGameSettingChange>? settingChanged = null)
    {
        DetectionKey = detectionKey;
        Profile = profile;
        _settings = settings;
        _save = save;
        _settingChanged = settingChanged;
        Icon = GameIconService.TryLoad(profile.DisplayName);
        // Nothing about the portrait touches the UI thread. This constructor
        // runs once per tab while the settings page is being built, and a
        // cached portrait is a 600x900 JPEG - decoding several of those inline
        // is what made the cards visibly late rather than the download did.
        _ = LoadPortraitAsync();
    }

    private async Task LoadPortraitAsync()
    {
        try
        {
            // Decode first: a portrait already on disk should appear on the
            // next frame, not after a round trip that will find nothing new.
            var portrait = await Task.Run(() => GamePortraitService.TryLoad(DisplayName)).ConfigureAwait(false);
            if (portrait is null)
            {
                if (!await GamePortraitService.EnsureCachedAsync(DetectionKey, DisplayName).ConfigureAwait(false)) return;
                portrait = await Task.Run(() => GamePortraitService.TryLoad(DisplayName)).ConfigureAwait(false);
                if (portrait is null) return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => Portrait = portrait);
        }
        catch (Exception error)
        {
            AppLog.Error($"Game portrait load failed for '{DisplayName}'", error);
        }
    }

    // Same identity GameCaptureOverrides uses - see CustomGameSettingsResolver.
    public string DetectionKey { get; }
    public CustomGameProfile Profile { get; }
    public string DisplayName => Profile.DisplayName;

    // Null for a game with no cached icon; the tab falls back to its initial.
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;

    private Bitmap? _portrait;

    /// <summary>Tall cover art, or null for a game none could be found for.</summary>
    public Bitmap? Portrait
    {
        get => _portrait;
        private set
        {
            if (!SetProperty(ref _portrait, value)) return;
            OnPropertyChanged(nameof(HasPortrait));
        }
    }

    public bool HasPortrait => _portrait is not null;
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

    public bool HasAnyGroup => Profile.Groups.Count > 0;

    /// <summary>
    /// False once every group is switched on. The Add Setting button hides
    /// rather than opening an empty menu - a control whose only outcome is
    /// "nothing to choose" is worse than no control.
    /// </summary>
    public bool CanAddGroup => Profile.Groups.Count < CustomGameSettingsResolver.AllGroups.Count;

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
        OnPropertyChanged(nameof(HasAnyGroup));
        OnPropertyChanged(nameof(CanAddGroup));
        NotifyQualityWarning();
        RaiseAllValues();
        _save();
        _settingChanged?.Invoke(CustomGameSettingChange.Group);
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
        _settingChanged?.Invoke(CustomGameSettingChange.RecordingMode);
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
            _qualityWarningAcknowledged = false;
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
                _qualityWarningAcknowledged = false;
                OnPropertyChanged(nameof(ReplayMaxHeight));
                OnPropertyChanged(nameof(ReplayFrameRate));
                OnPropertyChanged(nameof(ReplayBitrateMbps));
                OnPropertyChanged(nameof(SelectedBitrateOption));
                NotifyQualityWarning();
                _save();
                _settingChanged?.Invoke(CustomGameSettingChange.Quality);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomQuality));
        }
    }

    public bool IsCustomQuality => SelectedQualityPreset?.IsCustom ?? true;

    public bool QualityAboveRecommended =>
        HasQuality &&
        (Profile.ReplayMaxHeight > 1080 || Profile.ReplayFrameRate > 60 || Profile.ReplayBitrateMbps > 20);

    public bool QualityWarningVisible =>
        QualityAboveRecommended && !Profile.HideQualityWarning && !_qualityWarningAcknowledged;

    public string QualityWarningSummary
    {
        get
        {
            var exceeded = new List<string>();
            if (Profile.ReplayMaxHeight > 1080) exceeded.Add($"{Profile.ReplayMaxHeight}p");
            if (Profile.ReplayFrameRate > 60) exceeded.Add($"{Profile.ReplayFrameRate} FPS");
            if (Profile.ReplayBitrateMbps > 20) exceeded.Add($"{Profile.ReplayBitrateMbps} Mbps");
            return $"{DisplayName} exceeds ClypDat's recommended maximums: {string.Join(", ", exceeded)}.";
        }
    }

    public void FixQualityWarning()
    {
        if (!QualityAboveRecommended) return;

        Profile.ReplayMaxHeight = Math.Min(Profile.ReplayMaxHeight, 1080);
        Profile.ReplayFrameRate = Math.Min(Profile.ReplayFrameRate, 60);
        Profile.ReplayBitrateMbps = Math.Min(Profile.ReplayBitrateMbps, 20);
        _qualityWarningAcknowledged = false;
        OnPropertyChanged(nameof(ReplayMaxHeight));
        OnPropertyChanged(nameof(ReplayFrameRate));
        OnPropertyChanged(nameof(ReplayBitrateMbps));
        OnPropertyChanged(nameof(SelectedBitrateOption));
            RaiseQualityPreset();
            _save();
            _settingChanged?.Invoke(CustomGameSettingChange.Quality);
        _settingChanged?.Invoke(CustomGameSettingChange.Quality);
    }

    public void AcknowledgeQualityWarning()
    {
        if (!QualityWarningVisible) return;
        _qualityWarningAcknowledged = true;
        NotifyQualityWarning();
    }

    public void HideQualityWarning()
    {
        if (Profile.HideQualityWarning) return;
        Profile.HideQualityWarning = true;
        NotifyQualityWarning();
        _save();
    }

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
        NotifyQualityWarning();
    }

    private void NotifyQualityWarning()
    {
        OnPropertyChanged(nameof(QualityAboveRecommended));
        OnPropertyChanged(nameof(QualityWarningVisible));
        OnPropertyChanged(nameof(QualityWarningSummary));
    }

    public string ReplayFrameRateMode
    {
        get => Profile.ReplayFrameRateMode;
        set => Set(v => Profile.ReplayFrameRateMode = v, Profile.ReplayFrameRateMode, value);
    }

    // --- replay length and hotkey ----------------------------------------

    public int ReplayDurationSeconds => Profile.ReplayDurationSeconds;

    private ReplayDurationPreset? _selectedDurationPreset;

    // Null when the stored length matches no preset - only reachable from a
    // hand-edited settings file, and the pills simply show nothing selected
    // rather than silently rounding the user's value to the nearest one.
    public ReplayDurationPreset? SelectedDurationPreset
    {
        get => _selectedDurationPreset ??= MainWindowViewModel.DurationPresets
            .FirstOrDefault(preset => preset.Seconds == Profile.ReplayDurationSeconds);
        set
        {
            if (value is null || ReferenceEquals(_selectedDurationPreset, value)) return;
            _selectedDurationPreset = value;
            Profile.ReplayDurationSeconds = value.Seconds;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReplayDurationSeconds));
            _save();
            _settingChanged?.Invoke(CustomGameSettingChange.Replay);
        }
    }

    public string SaveReplayHotkey => Profile.SaveReplayHotkey;

    public void SetSaveReplayHotkey(string hotkey)
    {
        if (string.Equals(Profile.SaveReplayHotkey, hotkey, StringComparison.Ordinal)) return;
        Profile.SaveReplayHotkey = hotkey;
        OnPropertyChanged(nameof(SaveReplayHotkey));
        _save();
        _settingChanged?.Invoke(CustomGameSettingChange.Hotkey);
    }

    // --- audio ------------------------------------------------------------

    /// <summary>
    /// Per-app tracks for this game, mirroring the global Recording Audio
    /// list. Rebuilt from that list rather than discovered separately, so the
    /// two always offer the same apps in the same order - and so this does not
    /// need its own copy of the process enumeration.
    /// </summary>
    public ObservableCollection<AudioTrackProcessViewModel> AudioProcesses { get; } = new();

    public void SyncAudioProcesses(IEnumerable<AudioTrackProcessViewModel> template)
    {
        AudioProcesses.Clear();
        foreach (var source in template)
        {
            var enabled = Profile.AdditionalAudioProcesses.TryGetValue(source.Name, out var volume);
            var row = new AudioTrackProcessViewModel(source.Name, enabled, enabled ? volume : 100, OnAudioProcessChanged)
            {
                Icon = source.Icon
            };
            AudioProcesses.Add(row);
        }
    }

    private void OnAudioProcessChanged(AudioTrackProcessViewModel row)
    {
        if (row.IsEnabled) Profile.AdditionalAudioProcesses[row.Name] = (int)Math.Round(row.VolumePercent);
        else Profile.AdditionalAudioProcesses.Remove(row.Name);
        _save();
        _settingChanged?.Invoke(CustomGameSettingChange.Audio);
    }


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
            _qualityWarningAcknowledged = false;
            OnPropertyChanged(nameof(SelectedBitrateOption));
            RaiseQualityPreset();
        }

        _save();
        _settingChanged?.Invoke(property is nameof(GameAudioVolumePercent) or nameof(MicrophoneVolumePercent)
            or nameof(MicrophoneNoiseSuppressionEnabled) or nameof(MicrophoneNoiseGateThresholdDb)
            ? CustomGameSettingChange.Audio : CustomGameSettingChange.Quality);
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
        _selectedDurationPreset = null;
        OnPropertyChanged(nameof(SelectedDurationPreset));
        OnPropertyChanged(nameof(GameAudioVolumePercent));
        OnPropertyChanged(nameof(MicrophoneVolumePercent));
        OnPropertyChanged(nameof(MicrophoneNoiseSuppressionEnabled));
        OnPropertyChanged(nameof(MicrophoneNoiseGateThresholdDb));
    }
}

public enum CustomGameSettingChange
{
    Group,
    RecordingMode,
    Quality,
    Replay,
    Audio,
    Hotkey
}
