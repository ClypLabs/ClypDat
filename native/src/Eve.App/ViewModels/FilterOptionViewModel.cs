namespace Eve.App.ViewModels;

// A single checkable row in the Game Filters / Clip Type Filters dropdown
// checklists. Key is the stable filter-set identity (a game name, or one of
// the fixed clip-type keys); Label is what actually renders. IsChecked
// notifies the owner via callback rather than the owner polling the
// collection, since MainWindowViewModel needs to know the instant a
// checkbox flips to reapply IsMatchedByGameFilter/IsMatchedByClipTypeFilter
// across the library.
public sealed class FilterOptionViewModel : ViewModelBase
{
    private readonly Action<string, bool> _onCheckedChanged;
    private bool _isChecked;

    public FilterOptionViewModel(string key, string label, bool isChecked, Action<string, bool> onCheckedChanged)
    {
        Key = key;
        Label = label;
        _isChecked = isChecked;
        _onCheckedChanged = onCheckedChanged;
    }

    public string Key { get; }
    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value)) return;
            _onCheckedChanged(Key, value);
        }
    }

    // Lets the owner resync checked state (e.g. after a library refresh
    // rebuilds the option list) without going through the setter and
    // re-invoking the callback for a change the owner itself already knows
    // about.
    public void SetCheckedSilently(bool value)
    {
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }
}
