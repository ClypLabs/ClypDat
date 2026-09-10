namespace ClypDat.App.ViewModels;

public sealed class DiscoveredGameRowViewModel : ViewModelBase
{
    private string _displayName;
    public DiscoveredGameRowViewModel(string path, string displayName, string reason)
    {
        ExecutablePath = path; _displayName = displayName; Reason = reason;
    }
    public string ExecutablePath { get; }
    public string Reason { get; }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
}
