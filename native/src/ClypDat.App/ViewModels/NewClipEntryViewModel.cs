namespace ClypDat.App.ViewModels;

// One row in the "New Clips!" popup shown when a game closes. A thin wrapper
// around the real library card rather than binding the popup straight to it -
// exposes only the display properties this popup needs (PreviewImage,
// TileTopLabel, TileMainLabel, DurationLabel, CreatedAt, IsVod).
public sealed class NewClipEntryViewModel(ClipCardViewModel clip) : ViewModelBase
{
    public ClipCardViewModel Clip { get; } = clip;
    public string Path => Clip.Path;
    public bool IsVod => Clip.IsVod;
}
