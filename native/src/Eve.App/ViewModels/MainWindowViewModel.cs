using System.Collections.ObjectModel;
using Eve.App.Services;
using Eve.Core.Settings;

namespace Eve.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly MediaProbeService _mediaProbe = new();
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _isReplayRecording;
    private bool _isEditorVisible;
    private string _recorderStatus = "Replay Off";
    private string _activeGame = "No game detected";
    private string _selectedVideoName = "No video selected";
    private string _selectedVideoPath = string.Empty;
    private string _selectedThumbnailPath = string.Empty;
    private string _selectedMetadata = string.Empty;
    private double _cardWidth = 368;
    private int _cardColumns = 3;

    public MainWindowViewModel()
    {
        Settings = new AppSettings();
        ClipGroups = new ObservableCollection<ClipGroupViewModel>();
        TimelineTracks = new ObservableCollection<TrackLaneViewModel>();
    }

    public AppSettings Settings { get; }
    public ObservableCollection<ClipGroupViewModel> ClipGroups { get; }
    public ObservableCollection<TrackLaneViewModel> TimelineTracks { get; }

    public IEnumerable<ClipCardViewModel> AllClips => ClipGroups.SelectMany(group => group.Clips);

    public string LibraryHeaderDate => ClipGroups.Count > 0 ? ClipGroups[0].Label : "LIBRARY";
    public string LibraryHeaderGame => ClipGroups.Count > 0 ? "Videos" : "No folder selected";
    public string LibraryFolderDisplay => string.IsNullOrWhiteSpace(Settings.LibraryFolder)
        ? "Choose a folder"
        : Settings.LibraryFolder;

    public string LibraryLocationText => $"Location: {LibraryFolderDisplay}";

    public int SelectedCount => _selectedPaths.Count;
    public bool HasSelection => SelectedCount > 0;
    public bool HasNoSelection => !HasSelection;

    public string SelectionSummary
    {
        get
        {
            var selectedSize = AllClips
                .Where(clip => clip.IsSelected)
                .Sum(clip => clip.SizeBytes);
            return $"{SelectedCount} selected - {FormatBytes(selectedSize)}";
        }
    }

    public double CardWidth
    {
        get => _cardWidth;
        private set => SetProperty(ref _cardWidth, value);
    }

    public int CardColumns
    {
        get => _cardColumns;
        private set => SetProperty(ref _cardColumns, value);
    }

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

    public string SelectedThumbnailPath
    {
        get => _selectedThumbnailPath;
        private set => SetProperty(ref _selectedThumbnailPath, value);
    }

    public string SelectedMetadata
    {
        get => _selectedMetadata;
        private set => SetProperty(ref _selectedMetadata, value);
    }

    public async Task LoadLibraryFolderAsync(string folderPath)
    {
        Settings.LibraryFolder = folderPath;
        await RefreshLibraryAsync();
        IsEditorVisible = false;
    }

    public async Task RefreshLibraryAsync()
    {
        ClipGroups.Clear();
        ClearSelection();

        if (string.IsNullOrWhiteSpace(Settings.LibraryFolder) || !Directory.Exists(Settings.LibraryFolder))
        {
            NotifyLibraryChrome();
            return;
        }

        var clips = new List<ClipCardViewModel>();
        foreach (var file in _mediaProbe.EnumerateVideos(Settings.LibraryFolder))
        {
            try
            {
                clips.Add(new ClipCardViewModel(await _mediaProbe.ProbeAsync(file), _mediaProbe));
            }
            catch
            {
                var info = new FileInfo(file);
                var fallback = new MediaFileInfo(
                    Path.GetFileNameWithoutExtension(file),
                    file,
                    info.CreationTimeUtc,
                    TimeSpan.Zero,
                    info.Length,
                    string.Empty,
                    Array.Empty<MediaTrackInfo>(),
                    0,
                    0,
                    0);
                clips.Add(new ClipCardViewModel(fallback, _mediaProbe));
            }
        }

        foreach (var group in clips
                     .GroupBy(clip => clip.CreatedAt.ToLocalTime().Date)
                     .OrderByDescending(group => group.Key))
        {
            var label = group.Key.ToString("ddd, MMM d").ToUpperInvariant();
            ClipGroups.Add(new ClipGroupViewModel(group.Key.ToString("yyyy-MM-dd"), label, group));
        }

        NotifyLibraryChrome();
    }

    public async Task OpenVideoFileAsync(string filePath)
    {
        var media = await _mediaProbe.ProbeAsync(filePath);
        OpenMedia(media);
    }

    public void OpenClip(ClipCardViewModel clip)
    {
        OpenMedia(clip.Media);
    }

    public void UpdateCardLayout(double availableWidth)
    {
        var contentWidth = Math.Max(320, availableWidth - 48);
        var columns = contentWidth >= 1180 ? 3 : contentWidth >= 760 ? 2 : 1;
        CardColumns = columns;
        CardWidth = Math.Max(280, Math.Floor((contentWidth - ((columns - 1) * 24)) / columns));
    }

    public void SetClipSelected(ClipCardViewModel clip, bool selected)
    {
        clip.IsSelected = selected;
        if (selected) _selectedPaths.Add(clip.Path);
        else _selectedPaths.Remove(clip.Path);
        UpdateGroups();
        NotifySelectionChrome();
    }

    public void ToggleGroupSelection(ClipGroupViewModel group, bool selected)
    {
        foreach (var clip in group.Clips)
        {
            clip.IsSelected = selected;
            if (selected) _selectedPaths.Add(clip.Path);
            else _selectedPaths.Remove(clip.Path);
        }

        UpdateGroups();
        NotifySelectionChrome();
    }

    public async Task<int> DeleteSelectedAsync()
    {
        var selected = AllClips.Where(clip => clip.IsSelected).ToArray();
        foreach (var clip in selected)
        {
            File.Delete(clip.Path);
            _mediaProbe.DeleteCacheFor(clip.Path);
        }

        await RefreshLibraryAsync();
        return selected.Length;
    }

    private void OpenMedia(MediaFileInfo media)
    {
        SelectedVideoName = media.Name;
        SelectedVideoPath = media.Path;
        SelectedThumbnailPath = media.ThumbnailPath;
        SelectedMetadata = $"{media.Width}x{media.Height}, {media.Fps:0.#} FPS - {FormatBytes(media.SizeBytes)}";
        TimelineTracks.Clear();

        var hasVideo = false;
        foreach (var track in media.Tracks)
        {
            var color = track.Type switch
            {
                "video" => "#05C7B7",
                "audio" => "#2F9DD4",
                "subtitle" => "#CA8F1B",
                _ => "#607080"
            };
            if (track.Type == "video") hasVideo = true;
            TimelineTracks.Add(new TrackLaneViewModel(track.Label, track.Type, color));
        }

        if (!hasVideo)
        {
            TimelineTracks.Insert(0, new TrackLaneViewModel("Video", "video", "#05C7B7"));
        }

        IsEditorVisible = true;
    }

    private void ClearSelection()
    {
        _selectedPaths.Clear();
        NotifySelectionChrome();
    }

    private void UpdateGroups()
    {
        foreach (var group in ClipGroups)
        {
            var selectedCount = group.Clips.Count(clip => clip.IsSelected);
            group.IsSelected = selectedCount == group.Clips.Count && group.Clips.Count > 0;
            group.IsPartiallySelected = selectedCount > 0 && selectedCount < group.Clips.Count;
        }
    }

    private void NotifyLibraryChrome()
    {
        OnPropertyChanged(nameof(LibraryHeaderDate));
        OnPropertyChanged(nameof(LibraryHeaderGame));
        OnPropertyChanged(nameof(LibraryFolderDisplay));
        OnPropertyChanged(nameof(LibraryLocationText));
        NotifySelectionChrome();
    }

    private void NotifySelectionChrome()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
