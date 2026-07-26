using System.Collections.ObjectModel;

namespace ClypDat.App.ViewModels;

// A group tile in the sidebar's game rail - either one the user made (right-
// click "Move to folder" / "New Folder", or dragging a game onto another) or
// the automatic "More Games" folder the rail folds overflow into when nothing
// has been customised yet (see MainWindowViewModel.RebuildGameRail). Games is
// the SAME FilterOptionViewModel instances GameFilterOptions holds, not
// copies, so an icon landing or a checkbox change is visible here too without
// a rebuild.
public sealed class GameRailFolderViewModel : ViewModelBase
{
    public GameRailFolderViewModel(string id, string name, bool isAutomatic)
    {
        Id = id;
        _name = name;
        IsAutomatic = isAutomatic;
    }

    public string Id { get; }

    // True only for the synthetic overflow folder in automatic (uncustomised)
    // mode - it has no backing GameRailFolder in settings, can't be renamed or
    // ungrouped, and stops existing the instant the rail is customised (its
    // games become a real, persisted folder at that point instead).
    public bool IsAutomatic { get; }

    private string _name;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public ObservableCollection<FilterOptionViewModel> Games { get; } = new();

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    // Collapsed tile shows the first game's own icon (so it isn't a blank
    // folder glyph) plus a count badge - not a multi-icon mosaic, which would
    // need real screen time to size right and this can't get that before it
    // ships. Exposed as flat bool/string properties rather than binding
    // through PreviewGame.HasIcon etc directly in XAML, so there's no
    // null-chain binding to reason about at runtime.
    private FilterOptionViewModel? PreviewGame => Games.Count > 0 ? Games[0] : null;
    public bool IsEmpty => Games.Count == 0;
    public bool HasPreviewIcon => PreviewGame?.HasIcon == true;
    public bool HasPreviewInitial => PreviewGame is not null && !PreviewGame.HasIcon;
    public Avalonia.Media.Imaging.Bitmap? PreviewIcon => PreviewGame?.Icon;
    public string PreviewInitial => PreviewGame is null || string.IsNullOrEmpty(PreviewGame.Key)
        ? string.Empty
        : char.ToUpperInvariant(PreviewGame.Key[0]).ToString();
    public string CountLabel => Games.Count.ToString();

    public void NotifyGamesChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasPreviewIcon));
        OnPropertyChanged(nameof(HasPreviewInitial));
        OnPropertyChanged(nameof(PreviewIcon));
        OnPropertyChanged(nameof(PreviewInitial));
        OnPropertyChanged(nameof(CountLabel));
    }
}
