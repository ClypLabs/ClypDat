namespace ClypDat.App.ViewModels;

// One card in the "New Clips!" popup shown when a game closes.
//
// Wraps the real library card rather than binding the popup straight to it,
// because selection here means something different: ClipCardViewModel.IsSelected
// is the LIBRARY's selection, driving its selection bar, day-header tri-state
// and DeleteSelectedAsync. Ticking a box in this popup must not reach any of
// that, so hover/selection state lives on the wrapper and the card is exposed
// only for its display properties (PreviewImage, TileTopLabel, TileMainLabel,
// DurationLabel, CreatedAt, IsVod).
public sealed class NewClipEntryViewModel(ClipCardViewModel clip) : ViewModelBase
{
    private bool _isSelected;
    private bool _isHovered;
    private bool _showCheckBox;

    public ClipCardViewModel Clip { get; } = clip;
    public string Path => Clip.Path;
    public bool IsVod => Clip.IsVod;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            OnPropertyChanged(nameof(IsCheckVisible));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (!SetProperty(ref _isHovered, value)) return;
            OnPropertyChanged(nameof(IsCheckVisible));
        }
    }

    // False for a lone clip: with nothing to choose between, a checkbox is a
    // control that can only ever be turned on and off to no effect.
    public bool ShowCheckBox
    {
        get => _showCheckBox;
        set
        {
            if (!SetProperty(ref _showCheckBox, value)) return;
            OnPropertyChanged(nameof(IsCheckVisible));
        }
    }

    // Same rule as the library tile's own checkbox: visible while hovered, and
    // stays visible once ticked so a selection is never invisible.
    public bool IsCheckVisible => ShowCheckBox && (IsSelected || IsHovered);

    public event EventHandler? SelectionChanged;
}
