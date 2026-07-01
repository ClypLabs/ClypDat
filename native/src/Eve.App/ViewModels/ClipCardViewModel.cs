namespace Eve.App.ViewModels;

public sealed record ClipCardViewModel(
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    TimeSpan Duration,
    long SizeBytes)
{
    public string DateLabel => CreatedAt.ToString("MMM d, yyyy");
    public string DurationLabel => Duration > TimeSpan.Zero ? Duration.ToString("mm\\:ss") : "00:00";
    public string GameLabel => "VIDEO";
}
