using Eve.App.Services;

namespace Eve.App.ViewModels;

public sealed class MedalImportRowViewModel : ViewModelBase
{
    private bool _isSelected = true;

    public MedalImportRowViewModel(MedalClipRecord record)
    {
        Record = record;
    }

    public MedalClipRecord Record { get; }
    public string GameFolderName => Record.GameFolderName;
    public DateTime CreatedAtLocal => Record.CreatedAtUtc.ToLocalTime();
    public string RawTitle => string.IsNullOrWhiteSpace(Record.Title) ? Path.GetFileNameWithoutExtension(Record.VideoPath) : Record.Title;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
