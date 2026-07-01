using System.Collections.ObjectModel;
using Eve.Core.Settings;

namespace Eve.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv"
    };

    private bool _isReplayRecording;
    private bool _isEditorVisible;
    private string _recorderStatus = "Replay Off";
    private string _activeGame = "No game detected";
    private string _selectedVideoName = "No video selected";
    private string _selectedVideoPath = string.Empty;

    public MainWindowViewModel()
    {
        Settings = new AppSettings();
        Clips = new ObservableCollection<ClipCardViewModel>();
    }

    public AppSettings Settings { get; }
    public ObservableCollection<ClipCardViewModel> Clips { get; }

    public string LibraryHeaderDate => Clips.Count > 0
        ? Clips[0].CreatedAt.ToString("ddd, MMM d").ToUpperInvariant()
        : "LIBRARY";

    public string LibraryHeaderGame => Clips.Count > 0 ? "Videos" : "No folder selected";

    public string LibraryFolderDisplay => string.IsNullOrWhiteSpace(Settings.LibraryFolder)
        ? "Choose a folder"
        : Settings.LibraryFolder;

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

    public bool IsEditorVisible
    {
        get => _isEditorVisible;
        private set
        {
            if (!SetProperty(ref _isEditorVisible, value)) return;
            OnPropertyChanged(nameof(IsLibraryVisible));
        }
    }

    public bool IsLibraryVisible => !IsEditorVisible;

    public string SelectedVideoName
    {
        get => _selectedVideoName;
        private set => SetProperty(ref _selectedVideoName, value);
    }

    public string SelectedVideoPath
    {
        get => _selectedVideoPath;
        private set => SetProperty(ref _selectedVideoPath, value);
    }

    public void LoadLibraryFolder(string folderPath)
    {
        Settings.LibraryFolder = folderPath;
        RefreshLibrary();
        IsEditorVisible = false;
    }

    public void RefreshLibrary()
    {
        Clips.Clear();

        if (string.IsNullOrWhiteSpace(Settings.LibraryFolder) || !Directory.Exists(Settings.LibraryFolder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Settings.LibraryFolder)
                     .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
                     .OrderByDescending(File.GetCreationTimeUtc))
        {
            var info = new FileInfo(file);
            Clips.Add(new ClipCardViewModel(
                Path.GetFileNameWithoutExtension(file),
                file,
                info.CreationTimeUtc,
                TimeSpan.Zero,
                info.Length));
        }

        OnPropertyChanged(nameof(LibraryHeaderDate));
        OnPropertyChanged(nameof(LibraryHeaderGame));
        OnPropertyChanged(nameof(LibraryFolderDisplay));
    }

    public void OpenVideoFile(string filePath)
    {
        SelectedVideoPath = filePath;
        SelectedVideoName = Path.GetFileName(filePath);
        IsEditorVisible = true;
    }
}
