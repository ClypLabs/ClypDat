using Eve.App.Services;

namespace Eve.App.ViewModels;

public sealed class MedalImportRowViewModel : ViewModelBase
{
    private bool _isSelected;
    private TimeSpan _duration;
    private string _validationMessage = string.Empty;

    public MedalImportRowViewModel(MedalClipRecord record, bool stripEmoji)
    {
        Record = record;
        DisplayTitle = stripEmoji ? MedalImportService.StripEmoji(RawTitle) : RawTitle;
        _isSelected = true;
    }

    public MedalClipRecord Record { get; }
    public string GameFolderName => Record.GameFolderName;
    public DateTime CreatedAtLocal => Record.CreatedAtUtc.ToLocalTime();
    public string RawTitle => string.IsNullOrWhiteSpace(Record.Title) ? Path.GetFileNameWithoutExtension(Record.VideoPath) : Record.Title;
    public string DisplayTitle { get; }
    public TimeSpan Duration => _duration;
    public string ValidationMessage => _validationMessage;
    public bool CanImport => string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public void SetValidatedDuration(TimeSpan duration)
    {
        if (!SetProperty(ref _duration, duration, nameof(Duration))) return;
    }

    public void SetValidationError(string message)
    {
        if (!SetProperty(ref _validationMessage, message, nameof(ValidationMessage))) return;
        IsSelected = false;
        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(CanImport));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, CanImport && value);
    }
}
