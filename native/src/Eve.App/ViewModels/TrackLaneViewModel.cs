namespace Eve.App.ViewModels;

public sealed class TrackLaneViewModel
{
    public TrackLaneViewModel(string label, string type, string color)
    {
        Label = label;
        Type = type;
        Color = color;
    }

    public string Label { get; }
    public string Type { get; }
    public string Color { get; }
}
