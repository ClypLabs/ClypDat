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

    // What the port field shows. A valid port applies as it is typed; anything
    // else is never applied - the listener keeps the last working port, the
    // field says so, and leaving it puts that port back.
    private string? _oscPortText;
    public string OscPortText
    {
        get => _oscPortText ??= Settings.OscPort.ToString();
        set
        {
            if (!SetProperty(ref _oscPortText, value)) return;
            OscPortError = ValidateOscPortText(value, Settings.OscPort);
            if (OscPortError is null) OscPort = int.Parse(value);
        }
    }

    private string? _oscPortError;
    public string? OscPortError
    {
        get => _oscPortError;
        private set
        {
            if (!SetProperty(ref _oscPortError, value)) return;
            OnPropertyChanged(nameof(HasOscPortError));
        }
    }
    public bool HasOscPortError => _oscPortError is not null;

    internal void CommitOscPortText()
    {
        if (OscPortError is not null) OscPortText = Settings.OscPort.ToString();
    }

    internal static string? ValidateOscPortText(string? text, int currentPort)
    {
        var keeping = $"OSC stays on port {currentPort}.";
        if (string.IsNullOrWhiteSpace(text)) return $"Enter a port from 1 to 65535. {keeping}";
        if (!text.All(char.IsAsciiDigit)) return $"Use numbers only, from 1 to 65535. {keeping}";
        // Digits only, so a failed parse can only mean too large for an int.
        if (!int.TryParse(text, out var port) || port > 65535) return $"Ports go up to 65535. {keeping}";
        if (port < 1) return $"Ports start at 1. {keeping}";
        return null;
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
