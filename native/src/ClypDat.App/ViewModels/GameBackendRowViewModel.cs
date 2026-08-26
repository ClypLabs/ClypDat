using Avalonia;
using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

public sealed class GameBackendRowViewModel : ViewModelBase
{
    private bool _isVisible = true;
    private bool _showDivider;

    public GameBackendRowViewModel(string executableName, string displayName, string processName, bool isCustom)
    {
        ExecutableName = executableName;
        DisplayName = displayName;
        ProcessName = processName;
        IsCustom = isCustom;
    }

    // Identity key used for dedupe/removal/backend lookup - a Catalog-origin
    // row's ExecutableName is a detection key like "steam-381210", NOT a real
    // filename, so it must never be shown to the user as one.
    public string ExecutableName { get; }
    public string DisplayName { get; }
    // The actual exe filename shown as the row's subtitle - empty for rows
    // saved before this field existed, until the game is next detected.
    public string ProcessName { get; }
    public Thickness TitleMargin => string.IsNullOrWhiteSpace(ProcessName)
        ? new Thickness(0, -2, 0, 0)
        : new Thickness(0, 2, 0, 0);
    public bool IsCustom { get; }

    // The search box hides non-matching rows instead of rebuilding the collection.
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

}
