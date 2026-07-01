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
}
