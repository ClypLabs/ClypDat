using System.Collections.ObjectModel;
using Eve.Core.Clips;
using Eve.Core.Settings;

namespace Eve.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private bool _isReplayRecording;
    private string _recorderStatus = "Replay Off";
    private string _activeGame = "No game detected";

    public MainWindowViewModel()
    {
        Settings = new AppSettings();
        Clips = new ObservableCollection<ClipItem>();
    }

    public AppSettings Settings { get; }
    public ObservableCollection<ClipItem> Clips { get; }

    public bool IsReplayRecording
    {
        get => _isReplayRecording;
        set
        {
            if (!SetProperty(ref _isReplayRecording, value)) return;
            RecorderStatus = value ? "Replay On" : "Replay Off";
        }
    }

    public string RecorderStatus
    {
        get => _recorderStatus;
        set => SetProperty(ref _recorderStatus, value);
    }

    public string ActiveGame
    {
        get => _activeGame;
        set => SetProperty(ref _activeGame, value);
    }
}
