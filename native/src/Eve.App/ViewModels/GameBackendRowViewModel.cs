using Eve.App.Services;

namespace Eve.App.ViewModels;

public sealed class GameBackendRowViewModel : ViewModelBase
{
    private ReplayBackendPreset? _selectedBackend;

    public GameBackendRowViewModel(string executableName, string displayName, bool isCustom, bool isAntiCheatSensitive, ReplayBackendPreset selectedBackend)
    {
        ExecutableName = executableName;
        DisplayName = displayName;
        IsCustom = isCustom;
        IsAntiCheatSensitive = isAntiCheatSensitive;
        _selectedBackend = selectedBackend;
    }

    public string ExecutableName { get; }
    public string DisplayName { get; }
    public bool IsCustom { get; }
    public bool IsAntiCheatSensitive { get; }

    public ReplayBackendPreset? SelectedBackend
    {
        get => _selectedBackend;
        set
        {
            if (!SetProperty(ref _selectedBackend, value)) return;
            OnPropertyChanged(nameof(ShowAntiCheatWarning));
        }
    }

    // Anti-cheat-sensitive games default to Windows Capture for a reason (OBS's
    // hook gets blocked or the game's anti-cheat closes it outright) - warn
    // instead of silently letting the user walk into a black/frozen clip or a
    // closed game.
    public bool ShowAntiCheatWarning => IsAntiCheatSensitive &&
        SelectedBackend is not null &&
        !string.Equals(SelectedBackend.Value, "Legacy", StringComparison.OrdinalIgnoreCase);
}
