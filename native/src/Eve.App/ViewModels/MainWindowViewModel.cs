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
    private string _selectedSettingsSection = "Replay Buffer";
    private bool _wasEditorVisibleBeforeSettings;
    private bool _isCapturingHotkey;
    private AudioDeviceOption? _selectedChatAudioDevice;
    private AudioDeviceOption? _selectedMicrophoneDevice;
    private ProcessOption? _selectedChatProcess;
    private ProcessOption? _selectedProcessExclusion;
    private ReplayDurationPreset? _selectedReplayDurationPreset;
    private ResolutionOption? _selectedReplayResolution;
    private int _selectedReplayFrameRate;
    private ReplayBackendPreset? _selectedReplayBackend;
    private readonly string _initialReplayBackend;
    private string _newCustomGameExecutable = string.Empty;
    private string _gameSearchText = string.Empty;
    private string _newCustomGameDisplayName = string.Empty;
    private bool _replayBackendRestartRequired;
    private int _activeReplayMaxHeight;
    private int _activeReplayFrameRate;
    private bool _replayQualityRestartRequired;
    private string _selectedClipOverlayPosition = "Top Right";
    private string _selectedClipOverlayVolume = "Medium";
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
    private string _selectedCaptureBackend = string.Empty;
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
        ReplayResolutions = new ObservableCollection<ResolutionOption>
        {
            new("720p", 720),
            new("1080p", 1080),
            new("1440p", 1440),
            new("2160p (4K)", 2160)
        };
        ReplayFrameRates = new ObservableCollection<int> { 30, 60, 90, 120, 144, 165, 240 };
        ExportCodecs = new ObservableCollection<ExportCodecOption>
        {
            new("H.264", "h264_nvenc", "libx264"),
            new("H.265", "hevc_nvenc", "libx265"),
            new("AV1", "av1_nvenc", "libaom-av1")
        };
        ReplayBackends = new ObservableCollection<ReplayBackendPreset>
        {
            new("Auto", "Auto", "Uses OBS if available, otherwise falls back automatically - also knows which games need Windows Capture due to anti-cheat and switches for you."),
            new("OBS (best quality)", "Obs", "Highest quality and lowest overhead, but some anti-cheat games (e.g. CS2) need a launch option or may show a black/frozen capture."),
            new("Windows Capture (no game hook)", "Legacy", "Captures the screen directly with no process hook, so games with anti-cheat can get captured properly, at the cost of slightly higher overhead.")
        };
        ClipOverlayPositions = new ObservableCollection<string> { "Top Left", "Top Right" };
        ClipOverlayVolumes = new ObservableCollection<string> { "Low", "Medium", "High" };
        _selectedClipOverlayPosition = ClipOverlayPositions.FirstOrDefault(position => string.Equals(position, Settings.ClipOverlayPosition, StringComparison.OrdinalIgnoreCase)) ?? "Top Right";
        _selectedClipOverlayVolume = ClipOverlayVolumes.FirstOrDefault(volume => string.Equals(volume, Settings.ClipOverlayVolume, StringComparison.OrdinalIgnoreCase)) ?? "Medium";
        ExcludedProcesses = new ObservableCollection<string>(Settings.GameAudioExcludedProcesses);
        GameCaptureRows = new ObservableCollection<GameBackendRowViewModel>();
        RebuildGameCaptureRows();
        RefreshAudioDevices();
        SelectedReplayDurationPreset = ReplayDurationPresets.FirstOrDefault(preset => preset.Seconds == Settings.ReplayDurationSeconds) ??
                                       ReplayDurationPresets.First(preset => preset.Seconds == 60);
        _selectedReplayResolution = ReplayResolutions.FirstOrDefault(option => option.Height == Settings.ReplayMaxHeight) ??
                                     ReplayResolutions.First(option => option.Height == 1080);
        _selectedReplayFrameRate = ReplayFrameRates.Contains(Settings.ReplayFrameRate) ? Settings.ReplayFrameRate : 60;
        _activeReplayMaxHeight = Settings.ReplayMaxHeight;
        _activeReplayFrameRate = Settings.ReplayFrameRate;
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
    public ObservableCollection<ResolutionOption> ReplayResolutions { get; }
    public ObservableCollection<int> ReplayFrameRates { get; }
    public ObservableCollection<ReplayBackendPreset> ReplayBackends { get; }
    public ObservableCollection<ExportCodecOption> ExportCodecs { get; }
    public ObservableCollection<string> ExcludedProcesses { get; }
    public ObservableCollection<GameBackendRowViewModel> GameCaptureRows { get; }
    public ObservableCollection<GameBackendRowViewModel> FilteredGameCaptureRows { get; } = new();
    public ObservableCollection<string> ClipOverlayPositions { get; }
    public ObservableCollection<string> ClipOverlayVolumes { get; }

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
            if (value)
            {
                MarkReplayBufferRestarted();
            }
            else
            {
                ReplayQualityRestartRequired = false;
            }
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

    public string AppVersionDisplay => $"v{AppUpdateService.CurrentVersion}";

    public string SelectedSettingsSection
    {
        get => _selectedSettingsSection;
        set => SetProperty(ref _selectedSettingsSection, value);
    }

    public void SelectSettingsSection(string section) => SelectedSettingsSection = section;

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
            OnPropertyChanged(nameof(ReplayLengthLongCaptureWarning));
            SaveSettings();
        }
    }

    public bool ReplayLengthLongCaptureWarning =>
        (SelectedReplayDurationPreset?.Seconds ?? 0) >= 600 && !ReplayBackendIsObs;

    public ResolutionOption? SelectedReplayResolution
    {
        get => _selectedReplayResolution;
        set
        {
            if (!SetProperty(ref _selectedReplayResolution, value) || value is null) return;
            Settings.ReplayMaxHeight = value.Height;
            SaveSettings();
            UpdateReplayQualityRestartRequired();
            OnPropertyChanged(nameof(ReplayQualityAboveDefault));
        }
    }

    public int SelectedReplayFrameRate
    {
        get => _selectedReplayFrameRate;
        set
        {
            if (!SetProperty(ref _selectedReplayFrameRate, value)) return;
            Settings.ReplayFrameRate = value;
            SaveSettings();
            UpdateReplayQualityRestartRequired();
            OnPropertyChanged(nameof(ReplayQualityAboveDefault));
        }
    }

    public bool ReplayQualityAboveDefault => Settings.ReplayMaxHeight > 1080 || Settings.ReplayFrameRate > 60;

    private void UpdateReplayQualityRestartRequired()
    {
        ReplayQualityRestartRequired = IsReplayRecording &&
                                        (Settings.ReplayMaxHeight != _activeReplayMaxHeight || Settings.ReplayFrameRate != _activeReplayFrameRate);
    }

    public bool ReplayQualityRestartRequired
    {
        get => _replayQualityRestartRequired;
        private set => SetProperty(ref _replayQualityRestartRequired, value);
    }

    public void MarkReplayBufferRestarted()
    {
        _activeReplayMaxHeight = Settings.ReplayMaxHeight;
        _activeReplayFrameRate = Settings.ReplayFrameRate;
        ReplayQualityRestartRequired = false;
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
            OnPropertyChanged(nameof(ReplayBackendIsObs));
            OnPropertyChanged(nameof(ReplayLengthLongCaptureWarning));
        }
    }

    public bool ReplayBackendIsObs => string.Equals(SelectedReplayBackend?.Value, "Obs", StringComparison.OrdinalIgnoreCase);

    public bool ReplayBackendRestartRequired
    {
        get => _replayBackendRestartRequired;
        private set => SetProperty(ref _replayBackendRestartRequired, value);
    }

    public string SelectedClipOverlayPosition
    {
        get => _selectedClipOverlayPosition;
        set
        {
            if (!SetProperty(ref _selectedClipOverlayPosition, value)) return;
            Settings.ClipOverlayPosition = value;
            SaveSettings();
        }
    }

    public string SelectedClipOverlayVolume
    {
        get => _selectedClipOverlayVolume;
        set
        {
            if (!SetProperty(ref _selectedClipOverlayVolume, value)) return;
            Settings.ClipOverlayVolume = value;
            SaveSettings();
        }
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

    public bool EnableClipOverlay
    {
        get => Settings.EnableClipOverlay;
        set
        {
            if (Settings.EnableClipOverlay == value) return;
            Settings.EnableClipOverlay = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool EnableClipOverlaySound
    {
        get => Settings.EnableClipOverlaySound;
        set
        {
            if (Settings.EnableClipOverlaySound == value) return;
            Settings.EnableClipOverlaySound = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    private string _cs2GsiStatusText = string.Empty;

    public string Cs2GsiStatusText
    {
        get => _cs2GsiStatusText;
        set => SetProperty(ref _cs2GsiStatusText, value);
    }

    public bool Cs2AutoClipEnabled
    {
        get => Settings.Cs2AutoClip.Enabled;
        set
        {
            if (Settings.Cs2AutoClip.Enabled == value) return;
            Settings.Cs2AutoClip.Enabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool Cs2AllKills
    {
        get => Settings.Cs2AutoClip is { Kill: true, TwoKill: true, ThreeKill: true, FourKill: true, Ace: true };
        set
        {
            Settings.Cs2AutoClip.Kill = value;
            Settings.Cs2AutoClip.TwoKill = value;
            Settings.Cs2AutoClip.ThreeKill = value;
            Settings.Cs2AutoClip.FourKill = value;
            Settings.Cs2AutoClip.Ace = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Cs2Kill));
            OnPropertyChanged(nameof(Cs2TwoKill));
            OnPropertyChanged(nameof(Cs2ThreeKill));
            OnPropertyChanged(nameof(Cs2FourKill));
            OnPropertyChanged(nameof(Cs2Ace));
            OnPropertyChanged(nameof(Cs2EventsSummary));
            SaveSettings();
        }
    }

    public bool Cs2Kill
    {
        get => Settings.Cs2AutoClip.Kill;
        set { Settings.Cs2AutoClip.Kill = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2AllKills)); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2TwoKill
    {
        get => Settings.Cs2AutoClip.TwoKill;
        set { Settings.Cs2AutoClip.TwoKill = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2AllKills)); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2ThreeKill
    {
        get => Settings.Cs2AutoClip.ThreeKill;
        set { Settings.Cs2AutoClip.ThreeKill = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2AllKills)); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2FourKill
    {
        get => Settings.Cs2AutoClip.FourKill;
        set { Settings.Cs2AutoClip.FourKill = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2AllKills)); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2Ace
    {
        get => Settings.Cs2AutoClip.Ace;
        set { Settings.Cs2AutoClip.Ace = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2AllKills)); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2Headshot
    {
        get => Settings.Cs2AutoClip.Headshot;
        set { Settings.Cs2AutoClip.Headshot = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2Death
    {
        get => Settings.Cs2AutoClip.Death;
        set { Settings.Cs2AutoClip.Death = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2Assist
    {
        get => Settings.Cs2AutoClip.Assist;
        set { Settings.Cs2AutoClip.Assist = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    private bool _cs2CardExpanded;

    public bool Cs2CardExpanded
    {
        get => _cs2CardExpanded;
        set => SetProperty(ref _cs2CardExpanded, value);
    }

    private bool _cs2AllKillsExpanded;

    public bool Cs2AllKillsExpanded
    {
        get => _cs2AllKillsExpanded;
        set => SetProperty(ref _cs2AllKillsExpanded, value);
    }

    public string Cs2EventsSummary
    {
        get
        {
            var clip = Settings.Cs2AutoClip;
            var selected = new[] { clip.Kill, clip.TwoKill, clip.ThreeKill, clip.FourKill, clip.Ace, clip.Headshot, clip.Death, clip.Assist }.Count(value => value);
            return selected switch
            {
                0 => "No events selected",
                8 => "All events selected",
                _ => $"{selected} of 8 events selected"
            };
        }
    }

    public ObservableCollection<MedalImportRowViewModel> MedalImportRows { get; } = new();

    private bool _medalScanned;

    public bool MedalScanned
    {
        get => _medalScanned;
        set => SetProperty(ref _medalScanned, value);
    }

    private string _medalScanStatusText = string.Empty;

    public string MedalScanStatusText
    {
        get => _medalScanStatusText;
        set => SetProperty(ref _medalScanStatusText, value);
    }

    private bool _medalImportInProgress;

    public bool MedalImportInProgress
    {
        get => _medalImportInProgress;
        set => SetProperty(ref _medalImportInProgress, value);
    }

    private double _medalImportProgressPercent;

    public double MedalImportProgressPercent
    {
        get => _medalImportProgressPercent;
        set => SetProperty(ref _medalImportProgressPercent, value);
    }

    private string _medalImportStatusText = string.Empty;

    public string MedalImportStatusText
    {
        get => _medalImportStatusText;
        set => SetProperty(ref _medalImportStatusText, value);
    }

    public bool MedalImportStripEmoji
    {
        get => Settings.MedalImportStripEmoji;
        set { Settings.MedalImportStripEmoji = value; OnPropertyChanged(); SaveSettings(); }
    }

    public bool MedalImportCopyNotMove
    {
        get => Settings.MedalImportCopyNotMove;
        set { Settings.MedalImportCopyNotMove = value; OnPropertyChanged(); SaveSettings(); }
    }

    public void ScanForMedalClips()
    {
        MedalImportRows.Clear();
        IReadOnlyList<MedalClipRecord> found;
        try
        {
            found = MedalImportService.ScanForClips();
        }
        catch (Exception error)
        {
            MedalScanStatusText = $"Scan failed: {error.Message}";
            MedalScanned = true;
            return;
        }

        foreach (var record in found.OrderByDescending(record => record.CreatedAtUtc))
        {
            MedalImportRows.Add(new MedalImportRowViewModel(record));
        }

        MedalScanStatusText = found.Count switch
        {
            0 => "No Medal clips found.",
            1 => "1 Medal clip found.",
            _ => $"{found.Count} Medal clips found."
        };
        MedalScanned = true;
    }

    public async Task ImportSelectedMedalClipsAsync()
    {
        var selected = MedalImportRows.Where(row => row.IsSelected).ToList();
        if (selected.Count == 0) return;

        MedalImportInProgress = true;
        MedalImportProgressPercent = 0;
        var libraryFolder = Settings.LibraryFolder;
        var imported = 0;
        var failed = 0;

        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                var row = selected[i];
                MedalImportStatusText = $"Importing {i + 1} of {selected.Count}: {row.RawTitle}";
                try
                {
                    var title = MedalImportStripEmoji ? MedalImportService.StripEmoji(row.RawTitle) : row.RawTitle;
                    if (string.IsNullOrWhiteSpace(title)) title = row.GameFolderName;
                    var extension = Path.GetExtension(row.Record.VideoPath).TrimStart('.');
                    var fileName = ClipFileNaming.BuildFileName(title, row.CreatedAtLocal, extension);
                    var destinationPath = Path.Combine(libraryFolder, fileName);

                    await Task.Run(() =>
                    {
                        if (MedalImportCopyNotMove)
                        {
                            File.Copy(row.Record.VideoPath, destinationPath, overwrite: false);
                        }
                        else
                        {
                            File.Move(row.Record.VideoPath, destinationPath);
                        }
                    });

                    await AddOrUpdateLibraryClipAsync(destinationPath);
                    imported++;
                    MedalImportRows.Remove(row);
                }
                catch (Exception error)
                {
                    AppLog.Error($"Medal import failed for {row.Record.VideoPath}", error);
                    failed++;
                }

                MedalImportProgressPercent = (i + 1) * 100.0 / selected.Count;
            }
        }
        finally
        {
            MedalImportInProgress = false;
            MedalImportStatusText = failed == 0
                ? $"Imported {imported} clip{(imported == 1 ? "" : "s")}."
                : $"Imported {imported}, {failed} failed - see logs.";
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

    public string SelectedCaptureBackend
    {
        get => _selectedCaptureBackend;
        private set
        {
            if (!SetProperty(ref _selectedCaptureBackend, value)) return;
            OnPropertyChanged(nameof(HasSelectedCaptureBackend));
        }
    }

    public bool HasSelectedCaptureBackend => !string.IsNullOrWhiteSpace(SelectedCaptureBackend);

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
        SelectedCaptureBackend = string.Empty;
    }

    public void SaveSettings()
    {
        AppSettingsStore.Save(Settings);
    }

    public void SaveSelectedClipEditState()
    {
        if (string.IsNullOrWhiteSpace(SelectedVideoPath)) return;
        ClipEditSidecar.Save(SelectedVideoPath, new ClipEditSettings
        {
            TrimStartSeconds = Math.Max(0, TrimStart.TotalSeconds),
            TrimEndSeconds = Math.Max(0, TrimEnd.TotalSeconds),
            TrackVolumes = TimelineTracks
                .Where(track => track.IsAudio)
                .ToDictionary(track => track.StreamIndex, track => Math.Clamp(track.VolumePercent, 0, 150))
        });

        // One-time cleanup: drop the old settings.json-based copy now that this
        // clip's edit state lives in its own sidecar file instead.
        if (Settings.ClipEdits.Remove(ClipEditKey(SelectedVideoPath))) SaveSettings();
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
            .Select(file => new ClipCardViewModel(_mediaProbe.CreateLibraryStub(file)))
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
            var clip = new ClipCardViewModel(media);
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
            ClipEditSidecar.Delete(clip.Path);
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
        SelectedCaptureBackend = string.Empty;
    }

    public void OpenSettings()
    {
        _wasEditorVisibleBeforeSettings = IsEditorVisible;
        IsEditorVisible = false;
        IsSettingsVisible = true;
        SelectedSettingsSection = "Replay Buffer";
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

    public string NewCustomGameExecutable
    {
        get => _newCustomGameExecutable;
        set => SetProperty(ref _newCustomGameExecutable, value);
    }

    public string NewCustomGameDisplayName
    {
        get => _newCustomGameDisplayName;
        set => SetProperty(ref _newCustomGameDisplayName, value);
    }

    public string GameSearchText
    {
        get => _gameSearchText;
        set
        {
            if (!SetProperty(ref _gameSearchText, value)) return;
            ApplyGameSearchFilter();
        }
    }

    public event EventHandler? GameCatalogChanged;

    public void AddCustomGame()
    {
        var exe = Path.GetFileName(NewCustomGameExecutable.Trim());
        if (string.IsNullOrWhiteSpace(exe)) return;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        if (string.IsNullOrWhiteSpace(NewCustomGameDisplayName)) return;
        if (GameCatalog.BuiltIn.ContainsKey(exe)) return;

        Settings.GameCaptureOverrides.RemoveAll(g => string.Equals(g.ExecutableName, exe, StringComparison.OrdinalIgnoreCase));
        Settings.GameCaptureOverrides.Add(new GameCaptureOverride
        {
            ExecutableName = exe,
            DisplayName = NewCustomGameDisplayName.Trim(),
            CaptureBackend = "Auto"
        });
        NewCustomGameExecutable = string.Empty;
        NewCustomGameDisplayName = string.Empty;
        SaveSettings();
        RebuildGameCaptureRows();
        GameCatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveCustomGame(GameBackendRowViewModel row)
    {
        if (!row.IsCustom) return;
        Settings.GameCaptureOverrides.RemoveAll(g => string.Equals(g.ExecutableName, row.ExecutableName, StringComparison.OrdinalIgnoreCase));
        row.PropertyChanged -= GameCaptureRow_OnPropertyChanged;
        GameCaptureRows.Remove(row);
        SaveSettings();
        GameCatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildGameCaptureRows()
    {
        foreach (var row in GameCaptureRows) row.PropertyChanged -= GameCaptureRow_OnPropertyChanged;
        GameCaptureRows.Clear();

        foreach (var pair in GameCatalog.BuiltIn.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            var overrideEntry = Settings.GameCaptureOverrides.FirstOrDefault(g => string.Equals(g.ExecutableName, pair.Key, StringComparison.OrdinalIgnoreCase));
            var backend = ReplayBackends.FirstOrDefault(preset => string.Equals(preset.Value, overrideEntry?.CaptureBackend, StringComparison.OrdinalIgnoreCase))
                          ?? ReplayBackends.First(preset => preset.Value == "Auto");
            var row = new GameBackendRowViewModel(pair.Key, pair.Value, isCustom: false, GameCatalog.AntiCheatSensitive.Contains(pair.Key), backend);
            row.PropertyChanged += GameCaptureRow_OnPropertyChanged;
            GameCaptureRows.Add(row);
        }

        foreach (var overrideEntry in Settings.GameCaptureOverrides.Where(g => !GameCatalog.BuiltIn.ContainsKey(g.ExecutableName)))
        {
            var backend = ReplayBackends.FirstOrDefault(preset => string.Equals(preset.Value, overrideEntry.CaptureBackend, StringComparison.OrdinalIgnoreCase))
                          ?? ReplayBackends.First(preset => preset.Value == "Auto");
            var row = new GameBackendRowViewModel(overrideEntry.ExecutableName, overrideEntry.DisplayName, isCustom: true, GameCatalog.AntiCheatSensitive.Contains(overrideEntry.ExecutableName), backend);
            row.PropertyChanged += GameCaptureRow_OnPropertyChanged;
            GameCaptureRows.Add(row);
        }

        ApplyGameSearchFilter();
    }

    // Diffs into FilteredGameCaptureRows instead of Clear()-then-re-Add() so a row
    // that stays visible across a keystroke keeps the same container instead of
    // being torn down and rebuilt - a full reset was making per-row state (like the
    // anti-cheat warning) flicker away while typing in the search box.
    private void ApplyGameSearchFilter()
    {
        var query = GameSearchText.Trim();
        var matches = (string.IsNullOrWhiteSpace(query)
            ? GameCaptureRows
            : GameCaptureRows.Where(row => row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                            row.ExecutableName.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        for (var i = FilteredGameCaptureRows.Count - 1; i >= 0; i--)
        {
            if (!matches.Contains(FilteredGameCaptureRows[i])) FilteredGameCaptureRows.RemoveAt(i);
        }

        // matches is always a subsequence of the same fixed sorted GameCaptureRows
        // order, and so is FilteredGameCaptureRows after the removal pass above -
        // an item that's already present never needs to be reordered relative to
        // the others, so a plain forward Insert pass is enough. Deliberately not
        // using Move() here: it used to be used to relocate an already-present row
        // back into place, but this ItemsControl tears down and recreates that
        // row's container on a Move instead of just repositioning it - and the
        // recreated ComboBox's ItemsSource/SelectedItem bindings could resolve out
        // of order, leaving the backend dropdown showing blank ("Auto" vanishing)
        // even though the row's actual selection never changed.
        for (var i = 0; i < matches.Count; i++)
        {
            if (i < FilteredGameCaptureRows.Count && FilteredGameCaptureRows[i] == matches[i]) continue;
            FilteredGameCaptureRows.Insert(i, matches[i]);
        }
    }

    private void GameCaptureRow_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameBackendRowViewModel.SelectedBackend) || sender is not GameBackendRowViewModel row) return;

        var entry = Settings.GameCaptureOverrides.FirstOrDefault(g => string.Equals(g.ExecutableName, row.ExecutableName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new GameCaptureOverride { ExecutableName = row.ExecutableName, DisplayName = row.IsCustom ? row.DisplayName : string.Empty };
            Settings.GameCaptureOverrides.Add(entry);
        }

        entry.CaptureBackend = row.SelectedBackend?.Value ?? "Auto";
        SaveSettings();
    }

    public void RefreshAudioDevices()
    {
        ChatAudioDevices.Clear();
        foreach (var device in _audioDevices.GetRenderDevices(includeDisabled: true)) ChatAudioDevices.Add(device);
        MicrophoneDevices.Clear();
        var defaultMicName = _audioDevices.GetDefaultCaptureDeviceName();
        MicrophoneDevices.Add(new AudioDeviceOption(AudioDeviceOption.DefaultDeviceId,
            string.IsNullOrWhiteSpace(defaultMicName) ? "Default" : $"Default - {defaultMicName}"));
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
        var gameOverride = Settings.GameCaptureOverrides
            .FirstOrDefault(g => string.Equals(g.ExecutableName, ActiveGameDetection.ExeName, StringComparison.OrdinalIgnoreCase));
        var effectiveBackend = !string.IsNullOrWhiteSpace(gameOverride?.CaptureBackend) &&
                                !string.Equals(gameOverride.CaptureBackend, "Auto", StringComparison.OrdinalIgnoreCase)
            ? gameOverride.CaptureBackend
            : Settings.ReplayBackend;

        return new ReplayBufferConfig(
            SelectedReplayDurationPreset?.Seconds ?? Settings.ReplayDurationSeconds,
            Settings.ReplayMaxHeight,
            Settings.ReplayFrameRate,
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
            effectiveBackend,
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
            "-i", SelectedVideoPath
        };

        // The saved clip has game/chat/mic audio as separate discrete streams (so
        // the editor can mix/mute them independently), but a blanket "-map 0:a?"
        // just copies all of those streams into the output as separate tracks.
        // Most players and upload targets (Discord, X, a browser's <video>) only
        // play the first audio track of a multi-track file by default, so chat
        // and mic audio silently "disappeared" even though the export technically
        // contained them. Mix every audio track down to one, applying each
        // track's current volume, the same way editor playback already sounds.
        var audioTracks = TimelineTracks.Where(track => track.Type == "audio").ToArray();
        args.Add("-map");
        args.Add("0:v:0?");
        args.Add("-sn");

        if (audioTracks.Length == 1)
        {
            args.Add("-map");
            args.Add($"0:{audioTracks[0].StreamIndex}?");
            args.Add("-af");
            args.Add($"volume={VolumeMultiplier(audioTracks[0].VolumePercent):0.###}");
        }
        else if (audioTracks.Length > 1)
        {
            var filter = new System.Text.StringBuilder();
            var labels = new List<string>();
            foreach (var track in audioTracks)
            {
                var label = $"a{track.StreamIndex}";
                filter.Append($"[0:{track.StreamIndex}]volume={VolumeMultiplier(track.VolumePercent):0.###}[{label}];");
                labels.Add($"[{label}]");
            }

            filter.Append($"{string.Join(string.Empty, labels)}amix=inputs={audioTracks.Length}:normalize=0[aout]");
            args.Add("-filter_complex");
            args.Add(filter.ToString());
            args.Add("-map");
            args.Add("[aout]");
        }

        args.AddRange(BuildExportCodecArguments());
        if (audioTracks.Length > 0)
        {
            args.AddRange(new[] { "-c:a", "aac" });
        }

        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(outputPath);
        return args;
    }

    private static double VolumeMultiplier(double percent) => Math.Clamp(percent / 100d, 0, 1.5);

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
        SelectedCaptureBackend = string.IsNullOrWhiteSpace(media.CaptureBackend) ? string.Empty : $"Captured with: {media.CaptureBackend}";
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
        var edit = ClipEditSidecar.Load(path);
        if (edit is null)
        {
            if (!Settings.ClipEdits.TryGetValue(ClipEditKey(path), out edit)) return;
            // Migrate this clip's edit state out of settings.json and into its own
            // sidecar file the first time it's opened after upgrading.
            ClipEditSidecar.Save(path, edit);
            Settings.ClipEdits.Remove(ClipEditKey(path));
            SaveSettings();
        }
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
