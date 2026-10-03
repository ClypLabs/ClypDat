using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool OscEnabled
    {
        get => Settings.OscEnabled;
        set
        {
            if (Settings.OscEnabled == value) return;
            Settings.OscEnabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public int OscPort
    {
        get => Settings.OscPort;
        set
        {
            if (value is < 1 or > 65535 || Settings.OscPort == value) return;
            Settings.OscPort = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    private string _oscListenerStatus = "OSC disabled.";
    public string OscListenerStatus
    {
        get => _oscListenerStatus;
        internal set => SetProperty(ref _oscListenerStatus, value);
    }

    internal bool ApplyOscReplayDuration(int seconds)
    {
        var preset = DurationPresets.FirstOrDefault(p => p.Seconds == seconds);
        if (preset is null) return false;
        var key = IsEffectiveDesktopCapture || !ActiveGameDetection.IsDetected ? null :
            string.IsNullOrWhiteSpace(ActiveGameDetection.DetectionKey) ? ActiveGameDetection.ExeName : ActiveGameDetection.DetectionKey;
        var profile = CustomGameSettingsResolver.FindActive(Settings, key, CustomGameSettingsResolver.ReplayGroup);
        if (Settings.ReplayDurationSeconds == seconds && (profile is null || profile.ReplayDurationSeconds == seconds)) return false;

        Settings.ReplayDurationSeconds = seconds;
        _selectedReplayDurationPreset = preset;
        if (profile is not null)
        {
            profile.ReplayDurationSeconds = seconds;
            foreach (var tab in CustomGameTabs)
                if (ReferenceEquals(tab.Profile, profile)) tab.RefreshReplayDuration();
        }
        OnPropertyChanged(nameof(SelectedReplayDurationPreset));
        OnPropertyChanged(nameof(ReplayDurationSummary));
        SaveSettings();
        return true;
    }
}
