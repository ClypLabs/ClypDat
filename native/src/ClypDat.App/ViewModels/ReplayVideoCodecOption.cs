using Avalonia.Input;

namespace ClypDat.App.ViewModels;

// InputElement supplies the enabled state ComboBox checks on items during
// closed-dropdown navigation, before any ComboBoxItem container exists.
public sealed class ReplayVideoCodecOption : InputElement
{
    public ReplayVideoCodecOption(string label, string value, string description, bool isEnabled = true)
    {
        Label = label;
        Value = value;
        Description = description;
        IsEnabled = isEnabled;
    }

    public string Label { get; }
    public string Value { get; }
    public string Description { get; }
}
