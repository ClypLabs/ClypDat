using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

public sealed class GameBackendRowViewModel : ViewModelBase
{
    private ReplayBackendPreset? _selectedBackend;
    private bool _isVisible = true;

    public GameBackendRowViewModel(string executableName, string displayName, bool isCustom, ReplayBackendPreset selectedBackend)
    {
        ExecutableName = executableName;
        DisplayName = displayName;
        IsCustom = isCustom;
        _selectedBackend = selectedBackend;
    }

    public string ExecutableName { get; }
    public string DisplayName { get; }
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

    public ReplayBackendPreset? SelectedBackend
    {
        get => _selectedBackend;
        set
        {
            if (!SetProperty(ref _selectedBackend, value)) return;
        }
    }
}
