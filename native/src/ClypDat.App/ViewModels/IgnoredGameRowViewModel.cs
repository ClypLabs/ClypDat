namespace ClypDat.App.ViewModels;

// Excluded Games' row: Key is the detection key stored in
// Settings.IgnoredGameExecutables ("steam-381210") and is what actually gets
// un-excluded on Remove - DisplayName/ProcessName are only for showing the
// user something recognisable instead of that internal id.
public sealed class IgnoredGameRowViewModel
{
    public IgnoredGameRowViewModel(string key, string displayName, string processName)
    {
        Key = key;
        DisplayName = displayName;
        ProcessName = processName;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public string ProcessName { get; }
}
