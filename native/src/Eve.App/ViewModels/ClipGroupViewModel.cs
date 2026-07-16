using System.Collections.ObjectModel;

namespace Eve.App.ViewModels;

public sealed class ClipGroupViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _isPartiallySelected;

    public ClipGroupViewModel(string key, string label, IEnumerable<ClipCardViewModel> clips)
    {
        Key = key;
        Label = label;
        Clips = new ObservableCollection<ClipCardViewModel>(clips);
    }

    public string Key { get; }
    public string Label { get; }
    public ObservableCollection<ClipCardViewModel> Clips { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsPartiallySelected
    {
        get => _isPartiallySelected;
        set => SetProperty(ref _isPartiallySelected, value);
    }

    // Whole date group hides itself when a game or clip-type filter is
    // active and none of its clips match - each card's own
    // IsVisibleInLibrary flag (game AND clip-type match) drives this rather
    // than removing/re-adding items, avoiding container recreation churn
    // (same reasoning as the existing GameSearchText filter for Settings >
    // Game Capture Overrides).
    public bool HasVisibleClips => Clips.Any(c => c.IsVisibleInLibrary);

    public void NotifyFilterChanged() => OnPropertyChanged(nameof(HasVisibleClips));
}
