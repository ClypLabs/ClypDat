using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

public sealed class SteelSeriesImportRowViewModel : ViewModelBase
{
    private bool _isSelected = true;
    private TimeSpan _duration;
    private string _validationMessage = string.Empty;

    public SteelSeriesImportRowViewModel(SteelSeriesClipRecord record) => Record = record;
    public SteelSeriesClipRecord Record { get; }
    public string DisplayTitle => Record.Title;
    public string GameName => Record.GameName;
    public DateTime CreatedAtLocal => Record.CapturedAt.LocalDateTime;
    public TimeSpan Duration => _duration;
    public string ValidationMessage => _validationMessage;
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(_validationMessage);
    public bool CanImport => !HasValidationMessage;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, CanImport && value); }
    public void SetValidatedDuration(TimeSpan duration) => SetProperty(ref _duration, duration, nameof(Duration));
    public void SetValidationError(string message)
    {
        if (!SetProperty(ref _validationMessage, message, nameof(ValidationMessage))) return;
        IsSelected = false;
        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(CanImport));
    }
}
