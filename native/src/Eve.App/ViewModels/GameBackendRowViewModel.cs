using Eve.App.Services;

namespace Eve.App.ViewModels;

public sealed class GameBackendRowViewModel : ViewModelBase
{
    private ReplayBackendPreset? _selectedBackend;
    private bool _isVisible = true;

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

    // The search box hides non-matching rows by toggling this instead of removing
    // them from the bound collection - removing and later re-inserting a row tore
    // down its realized container, and the recreated ComboBox's ItemsSource
    // (bound via an ancestor Window.DataContext path) could finish resolving
    // after SelectedItem was applied, leaving the Capture Backend dropdown stuck
    // showing blank instead of "Auto" even though the row's actual selection was
    // untouched the whole time.
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public ReplayBackendPreset? SelectedBackend
    {
        get => _selectedBackend;
        set
        {
            if (!SetProperty(ref _selectedBackend, value)) return;
            OnPropertyChanged(nameof(ShowAntiCheatWarning));
        }
    }

    // Only OBS's process-hook capture is actually at risk from anti-cheat (it can
    // get blocked, or the game's anti-cheat can close the game outright) - Legacy
    // (Windows Capture) and EVE (Native) never hook the game process, so they're
    // both anti-cheat-safe and don't need this warning. Custom (user-added) games
    // also warn - GameCatalog.AntiCheatSensitive only covers the built-in catalog,
    // so a manually-added game's anti-cheat status is unknown and the safer
    // default is to flag it rather than silently assume it's fine.
    public bool ShowAntiCheatWarning => (IsAntiCheatSensitive || IsCustom) &&
        SelectedBackend is not null &&
        string.Equals(SelectedBackend.Value, "Obs", StringComparison.OrdinalIgnoreCase);
}
