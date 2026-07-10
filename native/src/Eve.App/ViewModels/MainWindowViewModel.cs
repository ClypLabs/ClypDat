using System.Collections.ObjectModel;
using Avalonia.Threading;
using Eve.App.Services;
using Eve.Core.Settings;

namespace Eve.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly MediaProbeService _mediaProbe = new();
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _libraryHydrationCts;
    private CancellationTokenSource? _waveformCts;
    private FileSystemWatcher? _libraryWatcher;
    private readonly DispatcherTimer _libraryRefreshDebounce;
    private readonly AudioDeviceService _audioDevices = new();
    private bool _isReplayRecording;
    private bool _isEditorVisible;
    private bool _isSettingsVisible;
    private bool _wasEditorVisibleBeforeSettings;
    private bool _isCapturingHotkey;
    private AudioDeviceOption? _selectedChatAudioDevice;
    private AudioDeviceOption? _selectedMicrophoneDevice;
    private ProcessOption? _selectedChatProcess;
    private ProcessOption? _selectedProcessExclusion;
    private ReplayDurationPreset? _selectedReplayDurationPreset;
    private ReplayQualityPreset? _selectedReplayQualityPreset;
    private ReplayBackendPreset? _selectedReplayBackend;
    private readonly string _initialReplayBackend;
    private bool _replayBackendRestartRequired;
    private ExportCodecOption? _selectedExportCodec;
    private string _recorderStatus = "Replay Off";
    private string _activeGame = "No game detected";
    private GameDetection _activeGameDetection = GameDetection.None;
    private string _selectedVideoName = "No video selected";
    private string _selectedVideoPath = string.Empty;
    private string _selectedThumbnailPath = string.Empty;
    private Avalonia.Media.Imaging.Bitmap? _selectedThumbnail;
    private string _selectedMetadata = string.Empty;
    private string _selectedCreated = "Created: No clip loaded";
    private string _selectedQuality = "Video Quality: Unknown";
    private string _selectedSize = "Size: 0 B";
    private string _editorTitle = string.Empty;
    private string _editorDescription = string.Empty;
    private TimeSpan _currentTime = TimeSpan.Zero;
    private TimeSpan _duration = TimeSpan.Zero;
    private TimeSpan _trimStart = TimeSpan.Zero;
    private TimeSpan _trimEnd = TimeSpan.Zero;
    private bool _isPlaying;
    private bool _isExporting;
    private double _cardWidth = 368;
    private double _cardImageHeight = 207;
    private int _cardColumns = 3;

    public MainWindowViewModel()
    {
        Settings = AppSettingsStore.Load();
        ClipGroups = new ObservableCollection<ClipGroupViewModel>();
        TimelineTracks = new ObservableCollection<TrackLaneViewModel>();
        ChatAudioDevices = new ObservableCollection<AudioDeviceOption>();
        MicrophoneDevices = new ObservableCollection<AudioDeviceOption>();
        OpenProcesses = new ObservableCollection<ProcessOption>();
        ReplayDurationPresets = new ObservableCollection<ReplayDurationPreset>
        {
            new("30s", 30),
            new("1 Minute", 60),
            new("2 Minutes", 120),
            new("3 Minutes", 180),
            new("4 Minutes", 240),
            new("5 Minutes", 300),
            new("10 Minutes", 600),
            new("15 Minutes", 900),
            new("20 Minutes", 1200)
        };
        ReplayQualityPresets = new ObservableCollection<ReplayQualityPreset>
        {
            new("Performance", 720, 30),
            new("Balanced", 1080, 30),
            new("Smooth", 1080, 60),
            new("Quality", 1440, 60)
        };
        ExportCodecs = new ObservableCollection<ExportCodecOption>
        {
            new("H.264", "h264_nvenc", "libx264"),
            new("H.265", "hevc_nvenc", "libx265"),
            new("AV1", "av1_nvenc", "libaom-av1")
        };
        ReplayBackends = new ObservableCollection<ReplayBackendPreset>
        {
            new("Auto", "Auto", "Uses OBS if available, otherwise falls back automatically."),
            new("OBS (best quality)", "Obs", "Highest quality and lowest overhead, but some anti-cheat games (e.g. CS2) need a launch option or may show a black/frozen capture."),
            new("Windows Capture (no game hook)", "Legacy", "Captures the screen directly with no process hook, so it isn't blocked by anti-cheat - works for CS2 without any launch option, at the cost of slightly higher overhead.")
        };
        ExcludedProcesses = new ObservableCollection<string>(Settings.GameAudioExcludedProcesses);
        RefreshAudioDevices();
        SelectedReplayDurationPreset = ReplayDurationPresets.FirstOrDefault(preset => preset.Seconds == Settings.ReplayDurationSeconds) ??
                                       ReplayDurationPresets.First(preset => preset.Seconds == 60);
        SelectedReplayQualityPreset = ReplayQualityPresets.FirstOrDefault(preset =>
                                          string.Equals(preset.Label, Settings.ReplayQualityPreset, StringComparison.OrdinalIgnoreCase) ||
                                          (preset.MaxHeight == Settings.ReplayMaxHeight && preset.FrameRate == Settings.ReplayFrameRate)) ??
                                      ReplayQualityPresets.First(preset => preset.Label == "Balanced");
        SelectedExportCodec = ExportCodecs.FirstOrDefault(codec => string.Equals(codec.Label, Settings.ExportVideoCodec, StringComparison.OrdinalIgnoreCase)) ??
                              ExportCodecs.First(codec => codec.Label == "H.264");
        _initialReplayBackend = string.IsNullOrWhiteSpace(Settings.ReplayBackend) ? "Auto" : Settings.ReplayBackend;
        _selectedReplayBackend = ReplayBackends.FirstOrDefault(preset => string.Equals(preset.Value, _initialReplayBackend, StringComparison.OrdinalIgnoreCase)) ??
                                  ReplayBackends.First(preset => preset.Value == "Auto");
        _libraryRefreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _libraryRefreshDebounce.Tick += async (_, _) =>
        {
            _libraryRefreshDebounce.Stop();
            await RefreshLibraryAsync();
        };
        _ = RefreshLibraryAsync();
    }

    public AppSettings Settings { get; }
    public ObservableCollection<ClipGroupViewModel> ClipGroups { get; }
    public ObservableCollection<TrackLaneViewModel> TimelineTracks { get; }
    public ObservableCollection<AudioDeviceOption> ChatAudioDevices { get; }
    public ObservableCollection<AudioDeviceOption> MicrophoneDevices { get; }
    public ObservableCollection<ProcessOption> OpenProcesses { get; }
    public ObservableCollection<ReplayDurationPreset> ReplayDurationPresets { get; }
    public ObservableCollection<ReplayQualityPreset> ReplayQualityPresets { get; }
    public ObservableCollection<ReplayBackendPreset> ReplayBackends { get; }
    public ObservableCollection<ExportCodecOption> ExportCodecs { get; }
    public ObservableCollection<string> ExcludedProcesses { get; }

    public IEnumerable<ClipCardViewModel> AllClips => ClipGroups.SelectMany(group => group.Clips);
    public int ReplayCaptureX { get; set; }
    public int ReplayCaptureY { get; set; }
    public int ReplayCaptureWidth { get; set; } = 1920;
    public int ReplayCaptureHeight { get; set; } = 1080;

    public string LibraryHeaderDate => ClipGroups.Count > 0 ? ClipGroups[0].Label : "LIBRARY";
    public string LibraryHeaderGame => ClipGroups.Count > 0 ? "Videos" : "No folder selected";
    public string LibraryTitle => "Clips";
    public string LibraryFolderDisplay => string.IsNullOrWhiteSpace(Settings.LibraryFolder)
        ? "Choose a folder"
        : Settings.LibraryFolder;

    public string LibraryLocationText => $"Location: {LibraryFolderDisplay}";
    public string HotkeyDisplay => IsCapturingHotkey ? "Press keys..." : Settings.SaveReplayHotkey;

    public int SelectedCount => _selectedPaths.Count;
    public bool HasSelection => SelectedCount > 0;
    public bool HasNoSelection => !HasSelection;
    public bool ShowLibraryActions => HasNoSelection && IsLibraryVisible;
    public bool ShowLibraryStatus => IsLibraryVisible;
    public bool ShowSettingsClose => IsSettingsVisible;

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

    public GameDetection ActiveGameDetection
    {
        get => _activeGameDetection;
        set => SetProperty(ref _activeGameDetection, value);
    }

    public bool IsEditorVisible
    {
        get => _isEditorVisible;
        private set
        {
            if (!SetProperty(ref _isEditorVisible, value)) return;
            OnPropertyChanged(nameof(IsLibraryVisible));
            OnPropertyChanged(nameof(IsSettingsVisible));
            OnPropertyChanged(nameof(ShowLibraryActions));
            OnPropertyChanged(nameof(ShowLibraryStatus));
        }
    }

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        private set
        {
            if (!SetProperty(ref _isSettingsVisible, value)) return;
            OnPropertyChanged(nameof(IsLibraryVisible));
            OnPropertyChanged(nameof(ShowLibraryActions));
            OnPropertyChanged(nameof(ShowLibraryStatus));
            OnPropertyChanged(nameof(ShowSettingsClose));
        }
    }

    public bool IsLibraryVisible => !IsEditorVisible && !IsSettingsVisible;

    public bool IsCapturingHotkey
    {
        get => _isCapturingHotkey;
        set
        {
            if (!SetProperty(ref _isCapturingHotkey, value)) return;
            OnPropertyChanged(nameof(HotkeyDisplay));
        }
    }

    public ReplayDurationPreset? SelectedReplayDurationPreset
    {
        get => _selectedReplayDurationPreset;
        set
        {
            if (!SetProperty(ref _selectedReplayDurationPreset, value) || value is null) return;
            Settings.ReplayDurationSeconds = value.Seconds;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public ReplayQualityPreset? SelectedReplayQualityPreset
    {
        get => _selectedReplayQualityPreset;
        set
        {
            if (!SetProperty(ref _selectedReplayQualityPreset, value) || value is null) return;
            Settings.ReplayQualityPreset = value.Label;
            Settings.ReplayMaxHeight = value.MaxHeight;
            Settings.ReplayFrameRate = value.FrameRate;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public ExportCodecOption? SelectedExportCodec
    {
        get => _selectedExportCodec;
        set
        {
            if (!SetProperty(ref _selectedExportCodec, value) || value is null) return;
            Settings.ExportVideoCodec = value.Label;
            SaveSettings();
        }
    }

    public ReplayBackendPreset? SelectedReplayBackend
    {
        get => _selectedReplayBackend;
        set
        {
            if (!SetProperty(ref _selectedReplayBackend, value) || value is null) return;
            Settings.ReplayBackend = value.Value;
            SaveSettings();
            ReplayBackendRestartRequired = !string.Equals(value.Value, _initialReplayBackend, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool ReplayBackendRestartRequired
    {
        get => _replayBackendRestartRequired;
        private set => SetProperty(ref _replayBackendRestartRequired, value);
    }

    public bool StartReplayOnLaunch
    {
        get => Settings.StartReplayOnLaunch;
        set
        {
            if (Settings.StartReplayOnLaunch == value) return;
            Settings.StartReplayOnLaunch = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool LaunchOnWindowsStartup
    {
        get => Settings.LaunchOnWindowsStartup;
        set
        {
            if (Settings.LaunchOnWindowsStartup == value) return;
            Settings.LaunchOnWindowsStartup = value;
            OnPropertyChanged();
            SaveSettings();
            StartupService.SetLaunchOnStartup(value, Settings.StartMinimizedToTray);
        }
    }

    public bool StartMinimizedToTray
    {
        get => Settings.StartMinimizedToTray;
        set
        {
            if (Settings.StartMinimizedToTray == value) return;
            Settings.StartMinimizedToTray = value;
            OnPropertyChanged();
            SaveSettings();
            if (Settings.LaunchOnWindowsStartup) StartupService.SetLaunchOnStartup(true, value);
        }
    }

    public AudioDeviceOption? SelectedChatAudioDevice
    {
        get => _selectedChatAudioDevice;
        set
        {
            if (!SetProperty(ref _selectedChatAudioDevice, value)) return;
            Settings.ChatAudioDeviceId = value?.Id ?? string.Empty;
            SaveSettings();
        }
    }

    public ProcessOption? SelectedChatProcess
    {
        get => _selectedChatProcess;
        set
        {
            if (!SetProperty(ref _selectedChatProcess, value)) return;
            Settings.ChatAudioProcessName = value?.Name ?? string.Empty;
            SaveSettings();
        }
    }

    public AudioDeviceOption? SelectedMicrophoneDevice
    {
        get => _selectedMicrophoneDevice;
        set
        {
            if (!SetProperty(ref _selectedMicrophoneDevice, value)) return;
            Settings.MicrophoneDeviceId = value?.Id ?? string.Empty;
            SaveSettings();
        }
    }

    public ProcessOption? SelectedProcessExclusion
    {
        get => _selectedProcessExclusion;
        set => SetProperty(ref _selectedProcessExclusion, value);
    }

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

    public Avalonia.Media.Imaging.Bitmap? SelectedThumbnail
    {
        get => _selectedThumbnail;
        private set => SetProperty(ref _selectedThumbnail, value);
    }

    public string SelectedMetadata
    {
        get => _selectedMetadata;
        private set => SetProperty(ref _selectedMetadata, value);
    }

    public string SelectedCreated
    {
        get => _selectedCreated;
        private set => SetProperty(ref _selectedCreated, value);
    }

    public string SelectedQuality
    {
        get => _selectedQuality;
        private set => SetProperty(ref _selectedQuality, value);
    }

    public string SelectedSize
    {
        get => _selectedSize;
        private set => SetProperty(ref _selectedSize, value);
    }

    public string EditorTitle
    {
        get => _editorTitle;
        set => SetProperty(ref _editorTitle, value);
    }

    public string EditorDescription
    {
        get => _editorDescription;
        set => SetProperty(ref _editorDescription, value);
    }

    public TimeSpan CurrentTime
    {
        get => _currentTime;
        set
        {
            if (!SetProperty(ref _currentTime, ClampTime(value))) return;
            OnTimelineChanged();
        }
    }

    public TimeSpan Duration
    {
        get => _duration;
        private set
        {
            if (!SetProperty(ref _duration, value < TimeSpan.Zero ? TimeSpan.Zero : value)) return;
            OnTimelineChanged();
        }
    }

    public TimeSpan TrimStart
    {
        get => _trimStart;
        set
        {
            var clamped = ClampTime(value);
            if (TrimEnd > TimeSpan.Zero && clamped > TrimEnd) clamped = TrimEnd;
            if (!SetProperty(ref _trimStart, clamped)) return;
            OnTimelineChanged();
        }
    }

    public TimeSpan TrimEnd
    {
        get => _trimEnd;
        set
        {
            var clamped = ClampTime(value);
            if (clamped < TrimStart) clamped = TrimStart;
            if (!SetProperty(ref _trimEnd, clamped)) return;
            OnTimelineChanged();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (!SetProperty(ref _isPlaying, value)) return;
            OnPropertyChanged(nameof(PlayPauseIcon));
        }
    }

    public bool IsExporting
    {
        get => _isExporting;
        set
        {
            if (!SetProperty(ref _isExporting, value)) return;
            OnPropertyChanged(nameof(ExportButtonText));
        }
    }

    public string PlayPauseIcon => IsPlaying ? "II" : ">";
    public string CurrentTimeLabel => FormatTime(CurrentTime);
    public string DurationLabel => FormatTime(Duration);
    public string TimelineStatusLabel => $"{CurrentTimeLabel} / {DurationLabel}";
    public string TrimStartPercent => Percent(TrimStart);
    public string TrimEndPercent => Percent(TrimEnd);
    public string PlayheadPercent => Percent(CurrentTime);
    public double TrimStartPercentValue => PercentValue(TrimStart);
    public double TrimEndPercentValue => PercentValue(TrimEnd);
    public double PlayheadPercentValue => PercentValue(CurrentTime);
    public string LeftShadeWidth => TrimStartPercent;
    public string RightShadeLeft => TrimEndPercent;
    public string RightShadeWidth => $"{Math.Max(0, 100 - PercentValue(TrimEnd)):0.###}%";
    public string ExportButtonText => IsExporting ? "Exporting..." : "Export";

    public async Task LoadLibraryFolderAsync(string folderPath)
    {
        Settings.LibraryFolder = folderPath;
        SaveSettings();
        await RefreshLibraryAsync();
        IsEditorVisible = false;
    }

    public void SaveSettings()
    {
        AppSettingsStore.Save(Settings);
    }

    public void SaveSelectedClipEditState()
    {
        if (string.IsNullOrWhiteSpace(SelectedVideoPath)) return;
        var key = ClipEditKey(SelectedVideoPath);
        Settings.ClipEdits[key] = new ClipEditSettings
        {
            TrimStartSeconds = Math.Max(0, TrimStart.TotalSeconds),
            TrimEndSeconds = Math.Max(0, TrimEnd.TotalSeconds),
            TrackVolumes = TimelineTracks
                .Where(track => track.IsAudio)
                .ToDictionary(track => track.StreamIndex, track => Math.Clamp(track.VolumePercent, 0, 150))
        };
        SaveSettings();
    }

    public Task RefreshLibraryAsync()
    {
        var scanClock = System.Diagnostics.Stopwatch.StartNew();
        ClipGroups.Clear();
        ClearSelection();
        StartLibraryWatcher();

        if (string.IsNullOrWhiteSpace(Settings.LibraryFolder) || !Directory.Exists(Settings.LibraryFolder))
        {
            NotifyLibraryChrome();
            return Task.CompletedTask;
        }

        var clips = _mediaProbe.EnumerateVideos(Settings.LibraryFolder)
            .Select(file => new ClipCardViewModel(_mediaProbe.CreateLibraryStub(file), _mediaProbe))
            .ToArray();

        foreach (var group in clips
                     .GroupBy(clip => clip.CreatedAt.ToLocalTime().Date)
                     .OrderByDescending(group => group.Key))
        {
            var label = group.Key.ToString("ddd, MMM d").ToUpperInvariant();
            ClipGroups.Add(new ClipGroupViewModel(group.Key.ToString("yyyy-MM-dd"), label, group));
        }

        NotifyLibraryChrome();
        StartLibraryHydration(clips);
        AppLog.Info($"Library refresh: {clips.Length} clips in {scanClock.ElapsedMilliseconds}ms.");
        return Task.CompletedTask;
    }

    public async Task AddOrUpdateLibraryClipAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var media = _mediaProbe.CreateLibraryStub(filePath);
        var existing = AllClips.FirstOrDefault(clip => string.Equals(clip.Path, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.UpdateMedia(media);
        }
        else
        {
            var clip = new ClipCardViewModel(media, _mediaProbe);
            var date = clip.CreatedAt.ToLocalTime().Date;
            var key = date.ToString("yyyy-MM-dd");
            var group = ClipGroups.FirstOrDefault(item => item.Key == key);
            if (group is null)
            {
                group = new ClipGroupViewModel(key, date.ToString("ddd, MMM d").ToUpperInvariant(), new[] { clip });
                var insertIndex = 0;
                while (insertIndex < ClipGroups.Count && string.CompareOrdinal(ClipGroups[insertIndex].Key, key) > 0) insertIndex++;
                ClipGroups.Insert(insertIndex, group);
            }
            else
            {
                var insertIndex = 0;
                while (insertIndex < group.Clips.Count && group.Clips[insertIndex].CreatedAt > clip.CreatedAt) insertIndex++;
                group.Clips.Insert(insertIndex, clip);
            }
        }

        NotifyLibraryChrome();
        AppLog.Info($"Library quick add: {filePath} in {clock.ElapsedMilliseconds}ms.");
        await HydrateOpenClipAsync(existing ?? AllClips.First(clip => string.Equals(clip.Path, filePath, StringComparison.OrdinalIgnoreCase)));
    }

    public void Dispose()
    {
        CancelLibraryHydration();
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;
        _libraryRefreshDebounce.Stop();
        _libraryWatcher?.Dispose();
        _libraryWatcher = null;
    }

    public Task OpenVideoFileAsync(string filePath)
    {
        CancelLibraryHydration();
        var media = _mediaProbe.CreateLibraryStub(filePath);
        OpenMedia(media);
        _ = HydrateSelectedMediaAsync(filePath);
        return Task.CompletedTask;
    }

    public Task OpenClipAsync(ClipCardViewModel clip)
    {
        CancelLibraryHydration();
        OpenMedia(clip.Media);

        if (clip.Duration == TimeSpan.Zero || clip.Media.Tracks.Count == 0)
        {
            _ = HydrateOpenClipAsync(clip);
        }

        return Task.CompletedTask;
    }

    public void UpdateCardLayout(double availableWidth)
    {
        var contentWidth = Math.Max(320, availableWidth - 48);
        CardColumns = 3;
        CardWidth = Math.Max(220, Math.Floor((contentWidth - 64) / 3));
        CardImageHeight = Math.Floor(CardWidth * 9 / 16);
    }

    public double CardImageHeight
    {
        get => _cardImageHeight;
        private set => SetProperty(ref _cardImageHeight, value);
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
            Settings.ClipEdits.Remove(ClipEditKey(clip.Path));
        }

        SaveSettings();
        await RefreshLibraryAsync();
        return selected.Length;
    }

    public void CloseEditor()
    {
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;
        IsPlaying = false;
        IsEditorVisible = false;
    }

    public void OpenSettings()
    {
        _wasEditorVisibleBeforeSettings = IsEditorVisible;
        IsEditorVisible = false;
        IsSettingsVisible = true;
    }

    public void CloseSettings()
    {
        IsSettingsVisible = false;
        IsEditorVisible = _wasEditorVisibleBeforeSettings && !string.IsNullOrWhiteSpace(SelectedVideoPath);
    }

    public void SetHotkey(string hotkey)
    {
        Settings.SaveReplayHotkey = hotkey;
        IsCapturingHotkey = false;
        OnPropertyChanged(nameof(HotkeyDisplay));
        SaveSettings();
    }

    public void AddExcludedProcess(string processName)
    {
        var normalized = Path.GetFileName(processName.Trim());
        if (string.IsNullOrWhiteSpace(normalized)) return;
        if (!normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) normalized += ".exe";
        if (Settings.GameAudioExcludedProcesses.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return;
        Settings.GameAudioExcludedProcesses.Add(normalized);
        ExcludedProcesses.Add(normalized);
        SaveSettings();
    }

    public void AddSelectedProcessExclusion()
    {
        if (SelectedProcessExclusion is null) return;
        AddExcludedProcess(SelectedProcessExclusion.Name);
    }

    public void RemoveExcludedProcess(string processName)
    {
        Settings.GameAudioExcludedProcesses.RemoveAll(item => string.Equals(item, processName, StringComparison.OrdinalIgnoreCase));
        ExcludedProcesses.Remove(processName);
        SaveSettings();
    }

    public void RefreshAudioDevices()
    {
        ChatAudioDevices.Clear();
        foreach (var device in _audioDevices.GetRenderDevices(includeDisabled: true)) ChatAudioDevices.Add(device);
        MicrophoneDevices.Clear();
        foreach (var device in _audioDevices.GetCaptureDevices()) MicrophoneDevices.Add(device);

        // Restore the saved selection for display without persisting a fallback over it:
        // the saved device id may just be temporarily missing from this enumeration pass
        // (driver reinit, USB replug), and overwriting Settings here would permanently
        // lose the user's real choice even after the device comes back.
        var chatMatch = ChatAudioDevices.FirstOrDefault(device => device.Id == Settings.ChatAudioDeviceId);
        SetProperty(ref _selectedChatAudioDevice, chatMatch ?? ChatAudioDevices.FirstOrDefault(), nameof(SelectedChatAudioDevice));

        var micMatch = MicrophoneDevices.FirstOrDefault(device => device.Id == Settings.MicrophoneDeviceId);
        SetProperty(ref _selectedMicrophoneDevice, micMatch ?? MicrophoneDevices.FirstOrDefault(), nameof(SelectedMicrophoneDevice));

        if (micMatch is null && MicrophoneDevices.Count > 0 && !string.IsNullOrWhiteSpace(Settings.MicrophoneDeviceId))
        {
            AppLog.Info($"Saved microphone device '{Settings.MicrophoneDeviceId}' not found this pass; showing '{_selectedMicrophoneDevice?.Name}' without changing the saved setting.");
        }
    }

    public async Task RefreshOpenProcessesAsync()
    {
        var selectedChatName = SelectedChatProcess?.Name ?? Settings.ChatAudioProcessName;
        var selectedName = SelectedProcessExclusion?.Name;
        var processes = await Task.Run(ProcessListService.GetOpenExecutables);
        OpenProcesses.Clear();
        foreach (var process in processes)
        {
            OpenProcesses.Add(process);
        }

        SelectedChatProcess = string.IsNullOrWhiteSpace(selectedChatName)
            ? null
            : OpenProcesses.FirstOrDefault(process => string.Equals(process.Name, selectedChatName, StringComparison.OrdinalIgnoreCase));
        SelectedProcessExclusion =
            OpenProcesses.FirstOrDefault(process => string.Equals(process.Name, selectedName, StringComparison.OrdinalIgnoreCase)) ??
            OpenProcesses.FirstOrDefault();
    }

    public ReplayBufferConfig CreateReplayConfig()
    {
        return new ReplayBufferConfig(
            SelectedReplayDurationPreset?.Seconds ?? Settings.ReplayDurationSeconds,
            SelectedReplayQualityPreset?.MaxHeight ?? Settings.ReplayMaxHeight,
            SelectedReplayQualityPreset?.FrameRate ?? Settings.ReplayFrameRate,
            ReplayCaptureX,
            ReplayCaptureY,
            ReplayCaptureWidth,
            ReplayCaptureHeight,
            string.Empty,
            string.Empty,
            SelectedChatProcess?.Name ?? Settings.ChatAudioProcessName,
            SelectedMicrophoneDevice?.Id ?? string.Empty,
            SelectedMicrophoneDevice?.Name ?? string.Empty,
            Settings.GameAudioExcludedProcesses.ToArray(),
            ActiveGameDetection.DisplayName,
            ActiveGameDetection.ExeName,
            ActiveGameDetection.WindowTitle,
            ActiveGameDetection.WindowClass,
            Settings.ReplayBackend,
            GameWindowHandle: ActiveGameDetection.WindowHandle);
    }

    public void SetDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        Duration = duration;
        if (TrimEnd <= TimeSpan.Zero || TrimEnd > duration)
        {
            TrimEnd = duration;
        }
    }

    public void SeekBySeconds(double seconds)
    {
        CurrentTime = CurrentTime + TimeSpan.FromSeconds(seconds);
    }

    public void RestartPlayback()
    {
        CurrentTime = TrimStart;
    }

    public IReadOnlyList<string> BuildExportArguments(string outputPath)
    {
        var startSeconds = Math.Max(0, TrimStart.TotalSeconds);
        var end = TrimEnd > TrimStart ? TrimEnd : Duration;
        var durationSeconds = Math.Max(0.1, (end - TrimStart).TotalSeconds);
        var args = new List<string>
        {
            "-y",
            "-ss", startSeconds.ToString("0.###"),
            "-t", durationSeconds.ToString("0.###"),
            "-i", SelectedVideoPath,
            "-map", "0:v:0?",
            "-map", "0:a?",
            "-sn"
        };

        args.AddRange(BuildExportCodecArguments());
        args.AddRange(new[] { "-c:a", "aac" });

        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(outputPath);
        return args;
    }

    private IReadOnlyList<string> BuildExportCodecArguments()
    {
        return SelectedExportCodec?.Label switch
        {
            "H.265" => new[] { "-c:v", "libx265", "-preset", "veryfast", "-crf", "24" },
            "AV1" => new[] { "-c:v", "libaom-av1", "-cpu-used", "6", "-crf", "32", "-b:v", "0" },
            _ => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20" }
        };
    }

    private void OpenMedia(MediaFileInfo media, bool preserveEditorText = false)
    {
        SelectedVideoName = media.Name;
        SelectedVideoPath = media.Path;
        SelectedThumbnailPath = media.ThumbnailPath;
        SelectedThumbnail = LoadBitmap(media.ThumbnailPath);
        if (!preserveEditorText)
        {
            EditorTitle = media.Name;
            EditorDescription = string.Empty;
        }
        SelectedCreated = $"Created: {media.CreatedAt.ToLocalTime():d MMM yyyy, H:mm}";
        SelectedQuality = media.Height > 0
            ? $"Video Quality: {ResolutionLabel(media.Height)}"
            : "Video Quality: Unknown";
        SelectedSize = $"Size: {FormatBytes(media.SizeBytes)}";
        SelectedMetadata = $"{SelectedQuality} - {SelectedSize}";
        Duration = media.Duration;
        CurrentTime = TimeSpan.Zero;
        TrimStart = TimeSpan.Zero;
        TrimEnd = media.Duration;
        IsPlaying = false;
        TimelineTracks.Clear();

        var hasVideo = false;
        var audioIndex = 0;
        foreach (var track in media.Tracks)
        {
            if (track.Type == "subtitle") continue;
            var color = track.Type switch
            {
                "video" => "#05C7B7",
                "audio" => AudioColor(audioIndex),
                _ => "#607080"
            };
            if (track.Type == "video") hasVideo = true;
            var label = track.Type == "audio"
                ? AudioLaneLabel(track.Label, audioIndex)
                : "Video";
            TimelineTracks.Add(new TrackLaneViewModel(track.Index, label, track.Type, color, track.Type == "audio", track.VolumePercent));
            if (track.Type == "audio") audioIndex++;
        }

        if (!hasVideo)
        {
            TimelineTracks.Insert(0, new TrackLaneViewModel(0, "Video", "video", "#05C7B7", false));
        }

        ApplyClipEditState(media.Path);
        IsEditorVisible = true;
        StartWaveformLoad(media);
    }

    private void ApplyClipEditState(string path)
    {
        if (!Settings.ClipEdits.TryGetValue(ClipEditKey(path), out var edit)) return;
        if (Duration > TimeSpan.Zero)
        {
            var start = TimeSpan.FromSeconds(Math.Clamp(edit.TrimStartSeconds, 0, Duration.TotalSeconds));
            var end = TimeSpan.FromSeconds(Math.Clamp(edit.TrimEndSeconds, 0, Duration.TotalSeconds));
            if (end <= TimeSpan.Zero || end < start) end = Duration;
            TrimStart = start;
            TrimEnd = end;
            CurrentTime = TrimStart;
        }

        foreach (var track in TimelineTracks.Where(track => track.IsAudio))
        {
            if (edit.TrackVolumes.TryGetValue(track.StreamIndex, out var volume))
            {
                track.VolumePercent = Math.Clamp(volume, 0, 150);
            }
        }
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
        OnPropertyChanged(nameof(LibraryTitle));
        OnPropertyChanged(nameof(LibraryFolderDisplay));
        OnPropertyChanged(nameof(LibraryLocationText));
        NotifySelectionChrome();
    }

    private void NotifySelectionChrome()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(ShowLibraryActions));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void StartLibraryHydration(IReadOnlyList<ClipCardViewModel> clips)
    {
        CancelLibraryHydration();
        _libraryHydrationCts = new CancellationTokenSource();
        _ = HydrateLibraryClipsAsync(clips, _libraryHydrationCts.Token);
    }

    private void CancelLibraryHydration()
    {
        _libraryHydrationCts?.Cancel();
        _libraryHydrationCts?.Dispose();
        _libraryHydrationCts = null;
    }

    private void StartLibraryWatcher()
    {
        _libraryWatcher?.Dispose();
        _libraryWatcher = null;

        if (string.IsNullOrWhiteSpace(Settings.LibraryFolder) || !Directory.Exists(Settings.LibraryFolder)) return;

        var watcher = new FileSystemWatcher(Settings.LibraryFolder)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
        };

        watcher.Created += LibraryWatcher_OnChanged;
        watcher.Deleted += LibraryWatcher_OnChanged;
        watcher.Renamed += LibraryWatcher_OnRenamed;
        _libraryWatcher = watcher;
    }

    private void LibraryWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!MediaProbeService.IsVideoFile(e.FullPath)) return;
        Dispatcher.UIThread.Post(ScheduleLibraryRefresh);
    }

    private void LibraryWatcher_OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!MediaProbeService.IsVideoFile(e.FullPath) && !MediaProbeService.IsVideoFile(e.OldFullPath)) return;
        Dispatcher.UIThread.Post(ScheduleLibraryRefresh);
    }

    private void ScheduleLibraryRefresh()
    {
        _libraryRefreshDebounce.Stop();
        _libraryRefreshDebounce.Start();
    }

    private void StartWaveformLoad(MediaFileInfo media)
    {
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = new CancellationTokenSource();
        _ = LoadWaveformsAsync(media, _waveformCts.Token);
    }

    private async Task LoadWaveformsAsync(MediaFileInfo media, CancellationToken cancellationToken)
    {
        try
        {
            var waveforms = await _mediaProbe.LoadWaveformsAsync(media, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!string.Equals(SelectedVideoPath, media.Path, StringComparison.OrdinalIgnoreCase)) return;
                foreach (var track in TimelineTracks.Where(track => track.IsAudio))
                {
                    if (waveforms.TryGetValue(track.StreamIndex, out var peaks))
                    {
                        track.WaveformPeaks = peaks;
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Another clip replaced this waveform load.
        }
        catch
        {
            // Missing waveforms should not block editing.
        }
    }

    private async Task HydrateOpenClipAsync(ClipCardViewModel clip)
    {
        try
        {
            var media = await _mediaProbe.ProbeAsync(clip.Path);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                clip.UpdateMedia(media);
                if (string.Equals(SelectedVideoPath, clip.Path, StringComparison.OrdinalIgnoreCase))
                {
                    OpenMedia(media, preserveEditorText: true);
                }
            });
        }
        catch
        {
            // Card stubs are enough to keep editor responsive when probe fails.
        }
    }

    private async Task HydrateSelectedMediaAsync(string filePath)
    {
        try
        {
            var media = await _mediaProbe.ProbeAsync(filePath);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (string.Equals(SelectedVideoPath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    OpenMedia(media, preserveEditorText: true);
                }
            });
        }
        catch
        {
            // File can still play even when metadata/thumbnail generation fails.
        }
    }

    private async Task HydrateLibraryClipsAsync(IReadOnlyList<ClipCardViewModel> clips, CancellationToken cancellationToken)
    {
        try
        {
            await Parallel.ForEachAsync(
                clips,
                new ParallelOptions { MaxDegreeOfParallelism = 1, CancellationToken = cancellationToken },
                async (clip, token) =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        var media = await _mediaProbe.ProbeAsync(clip.Path);
                        if (token.IsCancellationRequested) return;
                        await Dispatcher.UIThread.InvokeAsync(() => clip.UpdateMedia(media));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Bad files should not stop the rest of the library from filling in.
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // New folder/editor open superseded this scan.
        }
        finally
        {
            if (_libraryHydrationCts?.Token == cancellationToken)
            {
                _libraryHydrationCts.Dispose();
                _libraryHydrationCts = null;
            }
        }
    }

    private TimeSpan ClampTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) return TimeSpan.Zero;
        return Duration > TimeSpan.Zero && time > Duration ? Duration : time;
    }

    private void OnTimelineChanged()
    {
        OnPropertyChanged(nameof(CurrentTimeLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(TimelineStatusLabel));
        OnPropertyChanged(nameof(TrimStartPercent));
        OnPropertyChanged(nameof(TrimEndPercent));
        OnPropertyChanged(nameof(PlayheadPercent));
        OnPropertyChanged(nameof(TrimStartPercentValue));
        OnPropertyChanged(nameof(TrimEndPercentValue));
        OnPropertyChanged(nameof(PlayheadPercentValue));
        OnPropertyChanged(nameof(LeftShadeWidth));
        OnPropertyChanged(nameof(RightShadeLeft));
        OnPropertyChanged(nameof(RightShadeWidth));
    }

    private string Percent(TimeSpan time)
    {
        return $"{PercentValue(time):0.###}%";
    }

    private double PercentValue(TimeSpan time)
    {
        return Duration <= TimeSpan.Zero
            ? 0
            : Math.Clamp(time.TotalMilliseconds / Duration.TotalMilliseconds * 100, 0, 100);
    }

    private static Avalonia.Media.Imaging.Bitmap? LoadBitmap(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new Avalonia.Media.Imaging.Bitmap(path)
                : null;
        }
        catch
        {
            return null;
        }
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

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString("h\\:mm\\:ss")
            : time.ToString("m\\:ss");
    }

    private static string ClipEditKey(string path)
    {
        return Path.GetFullPath(path).ToUpperInvariant();
    }

    private static string ResolutionLabel(int height)
    {
        if (height >= 2160) return "4K";
        if (height >= 1440) return "1440p";
        if (height >= 1080) return "1080p";
        if (height >= 720) return "720p";
        return $"{height}p";
    }

    private static string AudioLabel(int audioIndex)
    {
        return audioIndex switch
        {
            0 => "Game Audio",
            1 => "Chat Audio",
            2 => "Microphone",
            _ => $"Audio {audioIndex + 1}"
        };
    }

    private static string AudioLaneLabel(string label, int audioIndex)
    {
        return string.IsNullOrWhiteSpace(label) ||
               label.StartsWith("Track", StringComparison.OrdinalIgnoreCase) ||
               label.StartsWith("Audio ", StringComparison.OrdinalIgnoreCase)
            ? AudioLabel(audioIndex)
            : label;
    }

    private static string AudioColor(int audioIndex)
    {
        return audioIndex switch
        {
            0 => "#05C7B7",
            1 => "#2F9DD4",
            2 => "#CA8F1B",
            _ => "#607080"
        };
    }
}
