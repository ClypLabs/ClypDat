using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

public sealed class GameBackendRowViewModel : ViewModelBase
{
    private ReplayBackendPreset? _selectedBackend;
    private bool _isVisible = true;
    private bool _showDivider;

    public GameBackendRowViewModel(string executableName, string displayName, string processName, bool isCustom, ReplayBackendPreset selectedBackend)
    {
        ExecutableName = executableName;
        DisplayName = displayName;
        ProcessName = processName;
        IsCustom = isCustom;
        _selectedBackend = selectedBackend;
    }

    // Identity key used for dedupe/removal/backend lookup - a Catalog-origin
    // row's ExecutableName is a detection key like "steam-381210", NOT a real
    // filename, so it must never be shown to the user as one.
    public string ExecutableName { get; }
    public string DisplayName { get; }
    // The actual exe filename shown as the row's subtitle - empty for rows
    // saved before this field existed, until the game is next detected.
    public string ProcessName { get; }
    public bool IsCustom { get; }

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

    public bool ShowDivider
    {
        get => _showDivider;
        set => SetProperty(ref _showDivider, value);
    }

    public ReplayBackendPreset? SelectedBackend
    {
        get => _selectedBackend;
        set
        {
            if (!SetProperty(ref _selectedBackend, value)) return;
        }
    }
}
