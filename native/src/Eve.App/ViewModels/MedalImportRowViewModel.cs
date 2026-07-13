using Eve.App.Services;

namespace Eve.App.ViewModels;

public sealed class MedalImportRowViewModel : ViewModelBase
{
    private bool _isSelected;

    public MedalImportRowViewModel(MedalClipRecord record, bool stripEmoji, TimeSpan duration, string validationMessage)
    {
        Record = record;
        Duration = duration;
        ValidationMessage = validationMessage;
        DisplayTitle = stripEmoji ? MedalImportService.StripEmoji(RawTitle) : RawTitle;
        _isSelected = CanImport;
    }

    public MedalClipRecord Record { get; }
    public string GameFolderName => Record.GameFolderName;
    public DateTime CreatedAtLocal => Record.CreatedAtUtc.ToLocalTime();
    public string RawTitle => string.IsNullOrWhiteSpace(Record.Title) ? Path.GetFileNameWithoutExtension(Record.VideoPath) : Record.Title;
    public string DisplayTitle { get; }
    public TimeSpan Duration { get; }
    public string ValidationMessage { get; }
    public bool CanImport => Duration > TimeSpan.Zero && string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, CanImport && value);
    }
}
