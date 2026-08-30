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
    private bool _showCheckBox = true;
    private int _selectionOrder;

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

    public int SelectionOrder
    {
        get => _selectionOrder;
        set
        {
            if (!SetProperty(ref _selectionOrder, value)) return;
            OnPropertyChanged(nameof(HasSelectionOrder));
        }
    }

    public bool HasSelectionOrder => SelectionOrder > 0;

    public event EventHandler? SelectionChanged;
}
