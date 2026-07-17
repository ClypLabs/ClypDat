using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Security.Cryptography;
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
    private readonly SemaphoreSlim _libraryLayoutMigrationLock = new(1, 1);
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
    private string _selectedClipFileNameScheme = ClipFileNaming.StandardScheme;
    private string _customClipFileNameTemplate = string.Empty;
    private string _clipFileNamePreview = string.Empty;
    private string _clipFileNameTemplateError = string.Empty;
    private bool _isRenamingAllClips;
    private string _renameAllClipsStatus = string.Empty;
    private ExportCodecOption? _selectedExportCodec;
    private string _recorderStatus = "Replay Off";
    private string _activeGame = "No game detected";
    private GameDetection _activeGameDetection = GameDetection.None;
    private string _selectedVideoName = "No video selected";
    private string _selectedVideoPath = string.Empty;
    private string _selectedThumbnailPath = string.Empty;
    private Avalonia.Media.Imaging.Bitmap? _selectedThumbnail;
    private bool _isEditorVideoLoading;
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
    private bool _isOnboardingVisible;
    private string _onboardingStep = "Replay Buffer";

    public MainWindowViewModel()
    {
        Settings = AppSettingsStore.Load();
        MedalImportRows.CollectionChanged += MedalImportRows_OnCollectionChanged;
        MigrateLegacyMedalImportHistory();
        AllClips = new ObservableCollection<ClipCardViewModel>();
        TimelineTracks = new ObservableCollection<TrackLaneViewModel>();
        ChatAudioDevices = new ObservableCollection<AudioDeviceOption>();
        MicrophoneDevices = new ObservableCollection<AudioDeviceOption>();
        OpenProcesses = new ObservableCollection<ProcessOption>();
        GameCandidateProcesses = new ObservableCollection<ProcessOption>();
        ReplayDurationPresets = new ObservableCollection<ReplayDurationPreset>
        {
            new("30s", 30),
            new("1 Minute", 60),
            new("2 Minutes", 120),
            new("3 Minutes", 180),
            new("4 Minutes", 240),
            new("5 Minutes", 300)
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
            new("Auto (recommended)", "Auto", "Uses EVE's own capture engine for every game - no process hook, so it's safe for anti-cheat-protected games too, with no stop/start gaps between segments."),
            new("EVE", "Native", "EVE's own capture engine - a true rolling buffer with no stop/start gaps between segments, and true per-window capture that keeps recording the game through alt-tabs and overlays. Used automatically on Auto."),
            new("OBS", "Obs", "Highest quality and lowest overhead, but some anti-cheat games (e.g. CS2) need a launch option or may show a black/frozen capture."),
            new("Windows Capture", "Legacy", "Captures the screen directly with no process hook, so games with anti-cheat can get captured properly, at the cost of slightly higher overhead.")
        };
        ClipOverlayPositions = new ObservableCollection<string> { "Top Left", "Top Right" };
        ClipOverlayVolumes = new ObservableCollection<string> { "Low", "Medium", "High" };
        ClipFileNameSchemes = new ObservableCollection<FileNameSchemeOption>
        {
            new("Standard", ClipFileNaming.StandardScheme),
            new("Readable", ClipFileNaming.ReadableScheme),
            new("Custom", ClipFileNaming.CustomScheme)
        };
        _selectedClipOverlayPosition = ClipOverlayPositions.FirstOrDefault(position => string.Equals(position, Settings.ClipOverlayPosition, StringComparison.OrdinalIgnoreCase)) ?? "Top Right";
        _selectedClipOverlayVolume = ClipOverlayVolumes.FirstOrDefault(volume => string.Equals(volume, Settings.ClipOverlayVolume, StringComparison.OrdinalIgnoreCase)) ?? "Medium";
        _selectedClipFileNameScheme = ClipFileNameSchemes.FirstOrDefault(item => string.Equals(item.Value, Settings.ClipFileNameScheme, StringComparison.OrdinalIgnoreCase))?.Value ?? ClipFileNaming.StandardScheme;
        _customClipFileNameTemplate = Settings.CustomClipFileNameTemplate;
        UpdateClipFileNamePreview();
        ExcludedProcesses = new ObservableCollection<string>(Settings.GameAudioExcludedProcesses);
        ChatAudioApps = new ObservableCollection<string>(Settings.ChatAudioProcessNames);
        SelectedMicrophones = new ObservableCollection<AudioDeviceOption>();
        GameCaptureRows = new ObservableCollection<GameBackendRowViewModel>();
        RebuildGameCaptureRows();
        SyncIgnoredGameExecutableRows();
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
    public ObservableCollection<ClipCardViewModel> AllClips { get; }
    public ObservableCollection<TrackLaneViewModel> TimelineTracks { get; }
    public ObservableCollection<AudioDeviceOption> ChatAudioDevices { get; }
    public ObservableCollection<AudioDeviceOption> MicrophoneDevices { get; }
    public ObservableCollection<ProcessOption> OpenProcesses { get; }
    // Narrower than OpenProcesses (which deliberately stays broad for the Chat
    // Audio App / exclusions pickers, where a browser or Discord is a valid
    // choice) - "Add a running game" only wants things that plausibly are a
    // game: not a browser/launcher/communication app, and not something already
    // tracked as a game (built-in catalog or an existing override).
    public ObservableCollection<ProcessOption> GameCandidateProcesses { get; }
    public ObservableCollection<ReplayDurationPreset> ReplayDurationPresets { get; }
    public ObservableCollection<ResolutionOption> ReplayResolutions { get; }
    public ObservableCollection<int> ReplayFrameRates { get; }
    public ObservableCollection<ReplayBackendPreset> ReplayBackends { get; }
    public ObservableCollection<ExportCodecOption> ExportCodecs { get; }
    public ObservableCollection<string> ExcludedProcesses { get; }
    public ObservableCollection<string> ChatAudioApps { get; }
    public ObservableCollection<AudioDeviceOption> SelectedMicrophones { get; }
    public ObservableCollection<GameBackendRowViewModel> GameCaptureRows { get; }
    public ObservableCollection<string> ClipOverlayPositions { get; }
    public ObservableCollection<string> ClipOverlayVolumes { get; }
    public ObservableCollection<FileNameSchemeOption> ClipFileNameSchemes { get; }

    public ObservableCollection<ThirdPartyLicenseEntry> ThirdPartyLicenseEntries { get; } = new()
    {
        new("OBS Studio", "https://github.com/obsproject/obs-studio", "GPLv2", "https://www.gnu.org/licenses/old-licenses/gpl-2.0.html"),
        new("VideoLAN", "https://code.videolan.org/videolan/vlc", "LGPLv2.1", "https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html"),
        new("FFmpeg", "https://ffmpeg.org", "GPLv2", "https://www.gnu.org/licenses/old-licenses/gpl-2.0.html"),
        new("ScreenRecorderLib", "https://github.com/sskodje/ScreenRecorderLib", "MIT License", "https://opensource.org/license/mit"),
        new("Avalonia", "https://github.com/AvaloniaUI/Avalonia", "MIT License", "https://opensource.org/license/mit"),
        new("NAudio", "https://github.com/naudio/NAudio", "MIT License", "https://opensource.org/license/mit"),
        new("Vortice.Windows", "https://github.com/amerkoleci/Vortice.Windows", "MIT License", "https://opensource.org/license/mit"),
        new("FFmpeg.AutoGen", "https://github.com/Ruslan-B/FFmpeg.AutoGen", "MIT License", "https://opensource.org/license/mit")
    };

    public int ReplayCaptureX { get; set; }
    public int ReplayCaptureY { get; set; }
    public int ReplayCaptureWidth { get; set; } = 1920;
    public int ReplayCaptureHeight { get; set; } = 1080;

    public string LibraryHeaderDate => AllClips.Count > 0 ? AllClips[0].DateHeaderLabel : "LIBRARY";
    public string LibraryHeaderGame => AllClips.Count > 0 ? "Videos" : "No folder selected";
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

    private bool _isVideoFullscreen;

    // True while the video-only fullscreen overlay (MainWindow.axaml) is
    // showing - set by the view via SetVideoFullscreen, not toggled
    // directly from XAML. The overlay reparents the SAME EditorVideoView
    // control into its own host rather than using a second VideoView -
    // hot-swapping MediaPlayer between two native video surfaces proved
    // unreliable (LibVLC never rendered a frame into the new one), and
    // running two live native surfaces at once raced for on-top-ness
    // (native surfaces don't respect Avalonia's managed z-order).
    public bool IsVideoFullscreen
    {
        get => _isVideoFullscreen;
        private set => SetProperty(ref _isVideoFullscreen, value);
    }

    public void SetVideoFullscreen(bool value) => IsVideoFullscreen = value;

    public bool IsEditorVideoAreaVisible => !IsEditorVideoLoading;

    // AppUpdateService.CurrentVersion is a System.Version, always 4 components -
    // our own <Version> in the csproj is 3-part (e.g. "0.1.1"), so the SDK-
    // generated AssemblyVersion pads a trailing ".0" that ToString() would show.
    public string AppVersionDisplay
    {
        get
        {
            var version = AppUpdateService.CurrentVersion;
            return $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

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
            SaveSettings();
        }
    }

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

    public string SelectedClipFileNameScheme
    {
        get => _selectedClipFileNameScheme;
        set
        {
            if (!SetProperty(ref _selectedClipFileNameScheme, value)) return;
            Settings.ClipFileNameScheme = value;
            UpdateClipFileNamePreview();
            SaveSettings();
            OnPropertyChanged(nameof(IsCustomClipFileNameScheme));
        }
    }

    public bool IsCustomClipFileNameScheme => string.Equals(SelectedClipFileNameScheme, ClipFileNaming.CustomScheme, StringComparison.OrdinalIgnoreCase);

    public string CustomClipFileNameTemplate
    {
        get => _customClipFileNameTemplate;
        set
        {
            if (!SetProperty(ref _customClipFileNameTemplate, value)) return;
            UpdateClipFileNamePreview();
            if (string.IsNullOrEmpty(ClipFileNameTemplateError))
            {
                Settings.CustomClipFileNameTemplate = value;
                SaveSettings();
            }
        }
    }

    public string ClipFileNamePreview
    {
        get => _clipFileNamePreview;
        private set => SetProperty(ref _clipFileNamePreview, value);
    }

    public string ClipFileNameTemplateError
    {
        get => _clipFileNameTemplateError;
        private set
        {
            if (!SetProperty(ref _clipFileNameTemplateError, value)) return;
            OnPropertyChanged(nameof(HasClipFileNameTemplateError));
            OnPropertyChanged(nameof(CanRenameAllClips));
        }
    }

    public bool HasClipFileNameTemplateError => !string.IsNullOrWhiteSpace(ClipFileNameTemplateError);
    public bool IsRenamingAllClips { get => _isRenamingAllClips; private set { if (SetProperty(ref _isRenamingAllClips, value)) OnPropertyChanged(nameof(CanRenameAllClips)); } }
    public string RenameAllClipsStatus
    {
        get => _renameAllClipsStatus;
        private set
        {
            if (!SetProperty(ref _renameAllClipsStatus, value)) return;
            OnPropertyChanged(nameof(HasRenameAllClipsStatus));
        }
    }
    public bool HasRenameAllClipsStatus => !string.IsNullOrWhiteSpace(RenameAllClipsStatus);
    public bool CanRenameAllClips => !IsRenamingAllClips && !HasClipFileNameTemplateError && !string.IsNullOrWhiteSpace(Settings.LibraryFolder) && Directory.Exists(Settings.LibraryFolder);

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

    // Three-state: true only when all five are on, false only when none are,
    // null (indeterminate - rendered as a filled box with a dash) for any
    // partial mix. IsHitTestVisible="False" in the XAML means this is purely
    // a reflected/decorative summary of the five sub-checkboxes below, not
    // itself directly clickable - the setter exists for completeness but
    // isn't reachable from the UI today.
    public bool? Cs2AllKills
    {
        get
        {
            var kills = Settings.Cs2AutoClip;
            var selectedCount = new[] { kills.Kill, kills.TwoKill, kills.ThreeKill, kills.FourKill, kills.Ace }.Count(selected => selected);
            if (selectedCount == 0) return false;
            if (selectedCount == 5) return true;
            return null;
        }
        set
        {
            var apply = value == true;
            Settings.Cs2AutoClip.Kill = apply;
            Settings.Cs2AutoClip.TwoKill = apply;
            Settings.Cs2AutoClip.ThreeKill = apply;
            Settings.Cs2AutoClip.FourKill = apply;
            Settings.Cs2AutoClip.Ace = apply;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Cs2Kill));
            OnPropertyChanged(nameof(Cs2TwoKill));
            OnPropertyChanged(nameof(Cs2ThreeKill));
            OnPropertyChanged(nameof(Cs2FourKill));
            OnPropertyChanged(nameof(Cs2Ace));
            OnPropertyChanged(nameof(Cs2AllKillsChecked));
            OnPropertyChanged(nameof(Cs2AllKillsIndeterminate));
            OnPropertyChanged(nameof(Cs2EventsSummary));
            SaveSettings();
        }
    }

    // Fluent's own indeterminate CheckBox glyph renders as a filled square,
    // not a dash - hand-drawn in XAML instead (checkmark/dash/empty-outline
    // Border+Path elements toggled by these) for a look that's actually a
    // dash, not dependent on the theme's own glyph choice.
    public bool Cs2AllKillsChecked => Cs2AllKills == true;
    public bool Cs2AllKillsIndeterminate => Cs2AllKills is null;

    public bool Cs2Kill
    {
        get => Settings.Cs2AutoClip.Kill;
        set { Settings.Cs2AutoClip.Kill = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2AllKills)); OnPropertyChanged(nameof(Cs2AllKillsChecked)); OnPropertyChanged(nameof(Cs2AllKillsIndeterminate)); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2TwoKill
    {
        get => Settings.Cs2AutoClip.TwoKill;
        set { Settings.Cs2AutoClip.TwoKill = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2AllKills)); OnPropertyChanged(nameof(Cs2AllKillsChecked)); OnPropertyChanged(nameof(Cs2AllKillsIndeterminate)); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2ThreeKill
    {
        get => Settings.Cs2AutoClip.ThreeKill;
        set { Settings.Cs2AutoClip.ThreeKill = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2AllKills)); OnPropertyChanged(nameof(Cs2AllKillsChecked)); OnPropertyChanged(nameof(Cs2AllKillsIndeterminate)); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2FourKill
    {
        get => Settings.Cs2AutoClip.FourKill;
        set { Settings.Cs2AutoClip.FourKill = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2AllKills)); OnPropertyChanged(nameof(Cs2AllKillsChecked)); OnPropertyChanged(nameof(Cs2AllKillsIndeterminate)); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
    }

    public bool Cs2Ace
    {
        get => Settings.Cs2AutoClip.Ace;
        set { Settings.Cs2AutoClip.Ace = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cs2AllKills)); OnPropertyChanged(nameof(Cs2AllKillsChecked)); OnPropertyChanged(nameof(Cs2AllKillsIndeterminate)); OnPropertyChanged(nameof(Cs2EventsSummary)); SaveSettings(); }
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

    public bool? MedalImportSelectionState
    {
        get
        {
            var selectable = MedalImportRows.Where(row => row.CanImport).ToArray();
            if (selectable.Length == 0 || selectable.All(row => !row.IsSelected)) return false;
            return selectable.All(row => row.IsSelected) ? true : null;
        }
    }

    public bool CanToggleMedalImportSelection => !MedalImportInProgress && MedalImportRows.Any(row => row.CanImport);

    public void ToggleMedalImportSelection()
    {
        var selectAll = MedalImportSelectionState != true;
        foreach (var row in MedalImportRows.Where(row => row.CanImport)) row.IsSelected = selectAll;
        NotifyMedalImportSelectionState();
    }

    private bool _medalScanned;

    public bool MedalScanned
    {
        get => _medalScanned;
        set => SetProperty(ref _medalScanned, value);
    }

    private string _medalScanStatusText = "Not scanned yet - click Scan for Medal Clips to look for clips Medal has recorded locally.";

    public string MedalScanStatusText
    {
        get => _medalScanStatusText;
        set => SetProperty(ref _medalScanStatusText, value);
    }

    private bool _medalImportInProgress;

    public bool MedalImportInProgress
    {
        get => _medalImportInProgress;
        set
        {
            if (!SetProperty(ref _medalImportInProgress, value)) return;
            OnPropertyChanged(nameof(ShowMedalImportStatusText));
            OnPropertyChanged(nameof(CanToggleMedalImportSelection));
        }
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
        set
        {
            if (!SetProperty(ref _medalImportStatusText, value)) return;
            OnPropertyChanged(nameof(ShowMedalImportStatusText));
        }
    }

    // Empty right after a scan (before any import has run) - showing this row
    // anyway just reserved a blank spacing slot between the scan header and the
    // results list below it.
    public bool ShowMedalImportStatusText => !MedalImportInProgress && !string.IsNullOrWhiteSpace(MedalImportStatusText);

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

    public async Task ScanForMedalClipsAsync()
    {
        MedalImportRows.Clear();
        IReadOnlyList<MedalClipRecord> found;
        MedalImportInProgress = true;
        MedalImportProgressPercent = 0;
        MedalImportStatusText = "Finding Medal clip catalogs...";
        IProgress<MedalScanProgress> progress = new Progress<MedalScanProgress>(update =>
        {
            if (!MedalImportInProgress) return;
            MedalImportProgressPercent = Math.Max(MedalImportProgressPercent, Math.Clamp(update.Percent, 0, 100));
            MedalImportStatusText = update.Status;
        });
        try
        {
            found = await Task.Run(() => MedalImportService.ScanForClips(progress));
        }
        catch (Exception error)
        {
            MedalScanStatusText = $"Scan failed: {error.Message}";
            MedalScanned = true;
            MedalImportInProgress = false;
            MedalImportStatusText = string.Empty;
            return;
        }

        try
        {
            var importedKeys = LoadMedalImportHistory();
            var repaired = await RepairMalformedMedalImportsAsync(found, importedKeys, progress);
            AddExistingMedalImportKeys(importedKeys);
            PersistMedalImportHistory(importedKeys);

            var candidates = found
                .GroupBy(MedalImportService.GetImportKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            var available = candidates
                .Where(record => !IsKnownMedalImport(record, importedKeys))
                .OrderByDescending(record => record.CreatedAtUtc)
                .ToArray();

            foreach (var record in available)
            {
                MedalImportRows.Add(new MedalImportRowViewModel(record, MedalImportStripEmoji));
            }

            var alreadyImported = candidates.Length - available.Length;
            var status = available.Length switch
            {
                0 when alreadyImported > 0 => $"No new Medal clips found ({alreadyImported} already imported).",
                0 => "No Medal clips found.",
                1 => alreadyImported > 0 ? $"1 new Medal clip found ({alreadyImported} already imported)." : "1 new Medal clip found.",
                _ => alreadyImported > 0 ? $"{available.Length} new Medal clips found ({alreadyImported} already imported)." : $"{available.Length} new Medal clips found."
            };
            if (repaired > 0) status += $" Repaired {repaired} malformed imported clip{(repaired == 1 ? "" : "s")}.";
            MedalScanStatusText = status;
            MedalScanned = true;
            progress.Report(new MedalScanProgress(100, "Medal scan complete."));
            MedalImportProgressPercent = 100;
        }
        catch (Exception error)
        {
            AppLog.Error("Medal import: scan processing failed.", error);
            MedalScanStatusText = $"Scan failed: {error.Message}";
            MedalScanned = true;
        }
        finally
        {
            MedalImportInProgress = false;
            MedalImportStatusText = string.Empty;
        }
    }

    private async Task<int> RepairMalformedMedalImportsAsync(IReadOnlyList<MedalClipRecord> sources, ISet<string> importedKeys, IProgress<MedalScanProgress> progress)
    {
        if (string.IsNullOrWhiteSpace(Settings.LibraryFolder) || !Directory.Exists(Settings.LibraryFolder)) return 0;

        var libraryRoot = Settings.LibraryFolder;
        var repaired = 0;
        var libraryVideos = Directory.EnumerateFiles(libraryRoot, "*.*", SearchOption.AllDirectories).Where(MediaProbeService.IsVideoFile).ToArray();
        for (var i = 0; i < libraryVideos.Length; i++)
        {
            var videoPath = libraryVideos[i];
            progress.Report(new MedalScanProgress(55 + 35.0 * (i + 1) / Math.Max(1, libraryVideos.Length), $"Checking library clip {i + 1} of {libraryVideos.Length}..."));
            var info = ClipInfoSidecar.Load(libraryRoot, videoPath);
            if (!NeedsMedalImportRepair(info)) continue;

            MedalClipRecord? source = null;
            try
            {
                var length = new FileInfo(videoPath).Length;
                foreach (var candidate in sources.Where(record => File.Exists(record.VideoPath) && new FileInfo(record.VideoPath).Length == length))
                {
                    if (await FilesMatchAsync(videoPath, candidate.VideoPath))
                    {
                        source = candidate;
                        break;
                    }
                }
            }
            catch (Exception error)
            {
                AppLog.Error($"Medal import repair: failed matching {videoPath}", error);
                continue;
            }

            if (source is null)
            {
                AppLog.Info($"Medal import repair: left unmatched clip unchanged: {videoPath}");
                continue;
            }

            try
            {
                var duration = await _mediaProbe.GetDurationAsync(videoPath);
                if (duration <= TimeSpan.Zero) throw new InvalidOperationException("Could not read the imported clip duration.");

                var title = string.IsNullOrWhiteSpace(info?.FileTitle)
                    || MedalImportService.IsLegacyMisparsedCounterStrike2Name(info.FileTitle)
                    || MedalImportService.IsDescriptiveTitle(info.FileTitle)
                    ? source.Title ?? source.GameFolderName
                    : info.FileTitle;
                var destinationDirectory = LibraryLayout.VideoDirectory(libraryRoot, duration, source.GameFolderName);
                Directory.CreateDirectory(destinationDirectory);
                var fileName = ClipFileNaming.BuildFileName(title, source.CreatedAtUtc.ToLocalTime(), Path.GetExtension(videoPath), Settings.ClipFileNameScheme, Settings.CustomClipFileNameTemplate, source.GameFolderName);
                var destinationPath = ClipFileNaming.BuildUniquePath(destinationDirectory, fileName);
                var oldKey = info!.MedalImportKey;

                File.Move(videoPath, destinationPath);
                LibraryLayout.MoveSidecars(libraryRoot, videoPath, destinationPath);
                File.SetCreationTimeUtc(destinationPath, source.CreatedAtUtc);
                File.SetLastWriteTimeUtc(destinationPath, source.CreatedAtUtc);
                _mediaProbe.DeleteCacheFor(videoPath);
                if (Settings.ClipEdits.Remove(ClipEditKey(videoPath), out var edit)) Settings.ClipEdits[ClipEditKey(destinationPath)] = edit;

                var newKey = MedalImportService.GetImportKey(source);
                ClipInfoSidecar.Save(libraryRoot, destinationPath, new ClipInfo(source.GameFolderName, info.AutoClipEventType, title, source.CreatedAtUtc, newKey));
                if (!string.IsNullOrWhiteSpace(oldKey)) importedKeys.Remove(oldKey);
                importedKeys.Add(newKey);
                repaired++;
            }
            catch (Exception error)
            {
                AppLog.Error($"Medal import repair: failed repairing {videoPath}", error);
            }
        }

        if (repaired > 0)
        {
            await RefreshLibraryAsync();
        }
        return repaired;
    }

    private static bool NeedsMedalImportRepair(ClipInfo? info) =>
        !string.IsNullOrWhiteSpace(info?.MedalImportKey) &&
        (MedalImportService.IsLegacyMisparsedCounterStrike2Name(info.GameDisplayName) ||
         MedalImportService.IsDescriptiveTitle(info.FileTitle) ||
         info.CapturedAt is { } captured && (captured.Year < 2000 || captured > DateTimeOffset.UtcNow.AddDays(2)));

    private static async Task<bool> FilesMatchAsync(string leftPath, string rightPath)
    {
        await using var left = File.OpenRead(leftPath);
        await using var right = File.OpenRead(rightPath);
        var leftHash = await SHA256.HashDataAsync(left);
        var rightHash = await SHA256.HashDataAsync(right);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private void MigrateLegacyMedalImportHistory()
    {
        if (Settings.LegacyImportedMedalClipKeys is not { Count: > 0 } ||
            string.IsNullOrWhiteSpace(Settings.LibraryFolder) ||
            !Directory.Exists(Settings.LibraryFolder)) return;

        if (!MedalImportHistoryStore.TryLoad(Settings.LibraryFolder, out var importedKeys)) return;
        importedKeys.UnionWith(Settings.LegacyImportedMedalClipKeys);
        if (!MedalImportHistoryStore.TrySave(Settings.LibraryFolder, importedKeys)) return;

        Settings.LegacyImportedMedalClipKeys = null;
        SaveSettings();
    }

    private HashSet<string> LoadMedalImportHistory()
    {
        var importedKeys = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(Settings.LibraryFolder) && Directory.Exists(Settings.LibraryFolder) &&
            MedalImportHistoryStore.TryLoad(Settings.LibraryFolder, out var savedKeys))
        {
            importedKeys.UnionWith(savedKeys);
        }

        if (Settings.LegacyImportedMedalClipKeys is { Count: > 0 }) importedKeys.UnionWith(Settings.LegacyImportedMedalClipKeys);
        return importedKeys;
    }

    private void PersistMedalImportHistory(ISet<string> importedKeys)
    {
        if (string.IsNullOrWhiteSpace(Settings.LibraryFolder) || !Directory.Exists(Settings.LibraryFolder)) return;
        if (!MedalImportHistoryStore.TrySave(Settings.LibraryFolder, importedKeys)) return;

        if (Settings.LegacyImportedMedalClipKeys is not { Count: > 0 }) return;
        Settings.LegacyImportedMedalClipKeys = null;
        SaveSettings();
    }

    private void AddExistingMedalImportKeys(ISet<string> importedKeys)
    {
        if (string.IsNullOrWhiteSpace(Settings.LibraryFolder)) return;
        var libraryRoot = Settings.LibraryFolder;

        try
        {
            foreach (var path in Directory.EnumerateFiles(libraryRoot, "*.*", SearchOption.AllDirectories).Where(MediaProbeService.IsVideoFile))
            {
                var sidecarKey = ClipInfoSidecar.Load(Settings.LibraryFolder, path)?.MedalImportKey;
                if (!string.IsNullOrWhiteSpace(sidecarKey))
                {
                    importedKeys.Add(sidecarKey);
                    continue;
                }

                var legacyRoot = Path.Combine(libraryRoot, "Imported Clips", "Medal");
                if (!path.StartsWith(legacyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;

                var info = new FileInfo(path);
                var game = Path.GetFileName(Path.GetDirectoryName(path)) ?? "Medal";
                importedKeys.Add(MedalImportService.GetImportKey(info.CreationTimeUtc, info.Length));
                importedKeys.Add(MedalImportService.GetLegacyImportKey(game, info.CreationTimeUtc, info.Length));
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Medal import: failed reading existing imported clips.", error);
        }
    }

    private void MedalImportRows_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.NewItems is not null)
        {
            foreach (MedalImportRowViewModel row in eventArgs.NewItems) row.PropertyChanged += MedalImportRow_OnPropertyChanged;
        }
        if (eventArgs.OldItems is not null)
        {
            foreach (MedalImportRowViewModel row in eventArgs.OldItems) row.PropertyChanged -= MedalImportRow_OnPropertyChanged;
        }
        NotifyMedalImportSelectionState();
    }

    private void MedalImportRow_OnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MedalImportRowViewModel.IsSelected) or nameof(MedalImportRowViewModel.CanImport)) NotifyMedalImportSelectionState();
    }

    private void NotifyMedalImportSelectionState()
    {
        OnPropertyChanged(nameof(MedalImportSelectionState));
        OnPropertyChanged(nameof(CanToggleMedalImportSelection));
    }

    private static bool IsKnownMedalImport(MedalClipRecord record, ISet<string> importedKeys)
    {
        var key = MedalImportService.GetImportKey(record);
        if (importedKeys.Contains(key)) return true;

        long length;
        try { length = new FileInfo(record.VideoPath).Length; }
        catch { return false; }
        return importedKeys.Contains(MedalImportService.GetLegacyImportKey(record.GameFolderName, record.CreatedAtUtc, length)) ||
               importedKeys.Contains(MedalImportService.GetLegacyImportKey("Medal", record.CreatedAtUtc, length));
    }

    public async Task ImportSelectedMedalClipsAsync()
    {
        var selected = MedalImportRows.Where(row => row.IsSelected && row.CanImport).ToList();
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
                MedalImportStatusText = $"Validating {i + 1} of {selected.Count}: {row.DisplayTitle}";
                MediaDurationProbeResult probe;
                try
                {
                    probe = await _mediaProbe.ProbeDurationAsync(row.Record.VideoPath);
                }
                catch (Exception error)
                {
                    var message = "Unreadable or incomplete video; Medal did not finish writing its metadata.";
                    row.SetValidationError(message);
                    AppLog.Error($"Medal import: unreadable source {row.Record.VideoPath}", error);
                    failed++;
                    MedalImportProgressPercent = (i + 1) * 100.0 / selected.Count;
                    continue;
                }

                if (probe.Duration <= TimeSpan.Zero)
                {
                    var message = "Unreadable or incomplete video; Medal did not finish writing its metadata.";
                    row.SetValidationError(message);
                    AppLog.Error($"Medal import: unreadable source {row.Record.VideoPath}: {probe.Error}");
                    failed++;
                    MedalImportProgressPercent = (i + 1) * 100.0 / selected.Count;
                    continue;
                }

                row.SetValidatedDuration(probe.Duration);
                MedalImportStatusText = $"Importing {i + 1} of {selected.Count}: {row.DisplayTitle}";
                try
                {
                    var title = MedalImportStripEmoji ? MedalImportService.StripEmoji(row.RawTitle) : row.RawTitle;
                    if (string.IsNullOrWhiteSpace(title)) title = row.GameFolderName;
                    var extension = Path.GetExtension(row.Record.VideoPath).TrimStart('.');
                    var fileName = ClipFileNaming.BuildFileName(title, row.CreatedAtLocal, extension, Settings.ClipFileNameScheme, Settings.CustomClipFileNameTemplate, row.GameFolderName);
                    var destinationDir = LibraryLayout.VideoDirectory(libraryFolder, row.Duration, row.GameFolderName);
                    Directory.CreateDirectory(destinationDir);
                    var destinationPath = ClipFileNaming.BuildUniquePath(destinationDir, fileName);

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

                        File.SetCreationTimeUtc(destinationPath, row.Record.CreatedAtUtc);
                        File.SetLastWriteTimeUtc(destinationPath, row.Record.CreatedAtUtc);
                    });

                    var importKey = MedalImportService.GetImportKey(row.Record);
                    ClipInfoSidecar.Save(Settings.LibraryFolder, destinationPath, new ClipInfo(row.GameFolderName, null, title, row.Record.CreatedAtUtc, importKey));
                    var importedKeys = LoadMedalImportHistory();
                    importedKeys.Add(importKey);
                    PersistMedalImportHistory(importedKeys);

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

    public bool IsStatusAreaVisible
    {
        get => Settings.IsStatusAreaVisible;
        set
        {
            if (Settings.IsStatusAreaVisible == value) return;
            Settings.IsStatusAreaVisible = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool ShowRecordingPausedIndicator
    {
        get => Settings.ShowRecordingPausedIndicator;
        set
        {
            if (Settings.ShowRecordingPausedIndicator == value) return;
            Settings.ShowRecordingPausedIndicator = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowRecordingPausedBadge));
            SaveSettings();
        }
    }

    private bool _isRecordingPausedAtCurrentTime;

    // Driven from MainWindow.axaml.cs's SyncPlaybackPosition against the
    // paused ranges loaded from the current clip's ".paused.json" sidecar
    // (see NativeReplayBuffer's DXGI Desktop Duplication capture - written
    // whenever the game window wasn't foreground during recording).
    public bool IsRecordingPausedAtCurrentTime
    {
        get => _isRecordingPausedAtCurrentTime;
        set
        {
            if (_isRecordingPausedAtCurrentTime == value) return;
            _isRecordingPausedAtCurrentTime = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowRecordingPausedBadge));
        }
    }

    public bool ShowRecordingPausedBadge => IsRecordingPausedAtCurrentTime && ShowRecordingPausedIndicator;

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

    // Single pick, persisted directly - this is what's actually used while
    // MultiChatAppEnabled is off (the common "at most one chat app" case), and
    // doubles as the picker for adding a new entry to ChatAudioApps once the
    // toggle is on.
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

    // Single pick, persisted directly - used while MultiMicrophoneEnabled is off
    // (the common one-microphone case), and doubles as the picker for adding a
    // new entry to SelectedMicrophones once the toggle is on.
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

    public bool MultiChatAppEnabled
    {
        get => Settings.MultiChatAppEnabled;
        set
        {
            if (Settings.MultiChatAppEnabled == value) return;
            Settings.MultiChatAppEnabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool MultiMicrophoneEnabled
    {
        get => Settings.MultiMicrophoneEnabled;
        set
        {
            if (Settings.MultiMicrophoneEnabled == value) return;
            Settings.MultiMicrophoneEnabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public void AddSelectedChatProcess()
    {
        var name = SelectedChatProcess?.Name;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (ChatAudioApps.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        ChatAudioApps.Add(name);
        Settings.ChatAudioProcessNames.Add(name);
        SaveSettings();
    }

    public void RemoveChatAudioApp(string name)
    {
        ChatAudioApps.Remove(name);
        Settings.ChatAudioProcessNames.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
    }

    public void AddSelectedMicrophone()
    {
        var device = SelectedMicrophoneDevice;
        if (device is null) return;
        if (SelectedMicrophones.Any(existing => existing.Id == device.Id)) return;
        SelectedMicrophones.Add(device);
        Settings.MicrophoneDeviceIds.Add(device.Id);
        SaveSettings();
    }

    public void RemoveMicrophone(string id)
    {
        var match = SelectedMicrophones.FirstOrDefault(device => device.Id == id);
        if (match is not null) SelectedMicrophones.Remove(match);
        Settings.MicrophoneDeviceIds.RemoveAll(item => item == id);
        SaveSettings();
    }

    public ProcessOption? SelectedProcessExclusion
    {
        get => _selectedProcessExclusion;
        set => SetProperty(ref _selectedProcessExclusion, value);
    }

    private ProcessOption? _selectedGameProcess;

    public ProcessOption? SelectedGameProcess
    {
        get => _selectedGameProcess;
        set => SetProperty(ref _selectedGameProcess, value);
    }

    public bool MicrophoneNoiseSuppressionEnabled
    {
        get => Settings.MicrophoneNoiseSuppressionEnabled;
        set { Settings.MicrophoneNoiseSuppressionEnabled = value; OnPropertyChanged(); SaveSettings(); }
    }

    public double MicrophoneNoiseSuppressionStrength
    {
        get => Settings.MicrophoneNoiseSuppressionStrength;
        set { Settings.MicrophoneNoiseSuppressionStrength = Math.Clamp(value, 0, 30); OnPropertyChanged(); SaveSettings(); }
    }

    public double AudioSyncOffsetMs
    {
        get => Settings.AudioSyncOffsetMs;
        set { Settings.AudioSyncOffsetMs = (int)Math.Clamp(value, -1000, 1000); OnPropertyChanged(); SaveSettings(); }
    }

    public bool FullSessionRecordingEnabled
    {
        get => Settings.FullSessionRecordingEnabled;
        set
        {
            Settings.FullSessionRecordingEnabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string FullSessionRecordingFolder
    {
        get => string.IsNullOrWhiteSpace(Settings.LibraryFolder) ? string.Empty : LibraryLayout.VodsRoot(Settings.LibraryFolder);
        set { }
    }

    public string FullSessionRecordingFolderDisplay =>
        string.IsNullOrWhiteSpace(FullSessionRecordingFolder) ? "Choose a library folder" : FullSessionRecordingFolder;

    public bool FullSessionBackgroundFinalize
    {
        get => Settings.FullSessionBackgroundFinalize;
        set
        {
            Settings.FullSessionBackgroundFinalize = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public IReadOnlyList<string> FullSessionCodecs { get; } = new[] { "H.264 (fastest)", "H.265 (smaller)", "AV1 (smallest)" };

    public string SelectedFullSessionCodec
    {
        get => FullSessionCodecs.FirstOrDefault(option => option.StartsWith(Settings.FullSessionVideoCodec, StringComparison.OrdinalIgnoreCase)) ?? FullSessionCodecs[0];
        set
        {
            Settings.FullSessionVideoCodec = value.Split(' ')[0];
            OnPropertyChanged();
            SaveSettings();
        }
    }

    // Gb = -1 is the "Custom" sentinel: the actual number comes from the
    // CustomFullSessionQuotaGb text field shown while it's selected.
    public sealed record FullSessionQuotaOption(string Label, int Gb);

    public IReadOnlyList<FullSessionQuotaOption> FullSessionQuotaOptions { get; } = new FullSessionQuotaOption[]
    {
        new("Unlimited", 0),
        new("25 GB", 25),
        new("50 GB", 50),
        new("100 GB", 100),
        new("250 GB", 250),
        new("500 GB", 500),
        new("Custom", -1)
    };

    private bool _customFullSessionQuotaSelected;

    public FullSessionQuotaOption SelectedFullSessionQuota
    {
        get
        {
            if (IsCustomFullSessionQuota) return FullSessionQuotaOptions[^1];
            return FullSessionQuotaOptions.FirstOrDefault(option => option.Gb == Settings.FullSessionQuotaGb) ?? FullSessionQuotaOptions[0];
        }
        set
        {
            if (value.Gb < 0)
            {
                _customFullSessionQuotaSelected = true;
                if (Settings.FullSessionQuotaGb <= 0) Settings.FullSessionQuotaGb = 500;
            }
            else
            {
                _customFullSessionQuotaSelected = false;
                Settings.FullSessionQuotaGb = value.Gb;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomFullSessionQuota));
            OnPropertyChanged(nameof(CustomFullSessionQuotaGb));
            SaveSettings();
        }
    }

    // Custom is active when explicitly picked, or when the saved value isn't
    // one of the presets (a previously-entered custom number surviving a
    // restart).
    public bool IsCustomFullSessionQuota =>
        _customFullSessionQuotaSelected ||
        (Settings.FullSessionQuotaGb > 0 && FullSessionQuotaOptions.All(option => option.Gb != Settings.FullSessionQuotaGb));

    public string CustomFullSessionQuotaGb
    {
        get => Settings.FullSessionQuotaGb > 0 ? Settings.FullSessionQuotaGb.ToString() : string.Empty;
        set
        {
            if (!int.TryParse(value, out var gb)) return;
            Settings.FullSessionQuotaGb = Math.Clamp(gb, 1, 100_000);
            OnPropertyChanged();
            SaveSettings();
        }
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

    // Drives a thumbnail placeholder over the editor's VideoView so opening a
    // clip shows its (already-decoded) thumbnail immediately instead of a
    // black frame for the second or so LibVLC needs to actually start
    // rendering - see StartEditorPlaybackAsync/the VideoPlayer.Playing hookup
    // in MainWindow.axaml.cs for where this gets set back to false.
    public bool IsEditorVideoLoading
    {
        get => _isEditorVideoLoading;
        set
        {
            if (!SetProperty(ref _isEditorVideoLoading, value)) return;
            OnPropertyChanged(nameof(IsEditorVideoAreaVisible));
        }
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

    // The actual timestamp behind SelectedCreated's display string - Export
    // uses this (not DateTime.Now) so the filename's date suffix always
    // reflects when the clip was actually recorded, not whenever Export
    // happened to be clicked.
    public DateTime SelectedCreatedAtLocal { get; private set; }

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
        MigrateLegacyMedalImportHistory();
        SaveSettings();
        OnPropertyChanged(nameof(CanRenameAllClips));
        await RefreshLibraryAsync();
        IsEditorVisible = false;
        SelectedCaptureBackend = string.Empty;
    }

    public void SaveSettings()
    {
        AppSettingsStore.Save(Settings);
    }

    public async Task RenameAllClipsAsync()
    {
        if (!CanRenameAllClips) return;
        IsRenamingAllClips = true;
        RenameAllClipsStatus = "Renaming library files...";
        try
        {
            var result = await Task.Run(() => RenameLibraryFiles());
            foreach (var (oldPath, newPath) in result.MovedPaths)
            {
                var oldKey = ClipEditKey(oldPath);
                if (Settings.ClipEdits.Remove(oldKey, out var edit)) Settings.ClipEdits[ClipEditKey(newPath)] = edit;
            }

            SaveSettings();
            RenameAllClipsStatus = $"Updated {result.Renamed} file(s); {result.Skipped} already matched; {result.Failed} failed.";
            await RefreshLibraryAsync();
        }
        finally
        {
            IsRenamingAllClips = false;
        }
    }

    private (int Renamed, int Skipped, int Failed, List<(string OldPath, string NewPath)> MovedPaths) RenameLibraryFiles()
    {
        var movedPaths = new List<(string OldPath, string NewPath)>();
        var renamed = 0;
        var skipped = 0;
        var failed = 0;
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(Settings.LibraryFolder, "*.*", SearchOption.AllDirectories)
                .Where(MediaProbeService.IsVideoFile)
                .ToArray();
        }
        catch (Exception error)
        {
            AppLog.Error("Clip filename migration: failed listing library files.", error);
            return (0, 0, 1, movedPaths);
        }

        foreach (var sourcePath in paths)
        {
            try
            {
                var card = new ClipCardViewModel(_mediaProbe.CreateLibraryStub(sourcePath), Settings.LibraryFolder);
                var info = ClipInfoSidecar.Load(Settings.LibraryFolder, sourcePath);
                var title = info?.FileTitle ?? card.GameNameLabel;
                var game = info?.GameDisplayName ?? card.GameFilterKey;
                var timestamp = info?.CapturedAt?.LocalDateTime ?? File.GetCreationTime(sourcePath);
                var directory = Path.GetDirectoryName(sourcePath) ?? Settings.LibraryFolder;
                var fileName = ClipFileNaming.BuildFileName(title, timestamp, Path.GetExtension(sourcePath), Settings.ClipFileNameScheme, Settings.CustomClipFileNameTemplate, game);
                var targetPath = Path.Combine(directory, fileName);
                if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                targetPath = ClipFileNaming.BuildUniquePath(directory, fileName);
                // Store naming metadata before moving so future scheme changes do
                // not have to reverse-engineer a user-defined template.
                ClipInfoSidecar.Save(Settings.LibraryFolder, sourcePath, new ClipInfo(game, info?.AutoClipEventType, title, timestamp, info?.MedalImportKey));
                File.Move(sourcePath, targetPath);
                MoveClipSidecars(sourcePath, targetPath);
                _mediaProbe.DeleteCacheFor(sourcePath);
                movedPaths.Add((sourcePath, targetPath));
                renamed++;
            }
            catch (Exception error)
            {
                AppLog.Error($"Clip filename migration: failed renaming {sourcePath}", error);
                failed++;
            }
        }

        return (renamed, skipped, failed, movedPaths);
    }

    private void UpdateClipFileNamePreview()
    {
        var timestamp = new DateTime(2025, 6, 26, 22, 40, 59);
        if (string.Equals(SelectedClipFileNameScheme, ClipFileNaming.CustomScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (ClipFileNaming.TryBuildPreview(CustomClipFileNameTemplate, timestamp, "Counter Strike 2", "Counter Strike 2", out var preview, out var error))
            {
                ClipFileNamePreview = $"{preview}.mp4";
                ClipFileNameTemplateError = string.Empty;
            }
            else
            {
                ClipFileNamePreview = "Invalid template";
                ClipFileNameTemplateError = error;
            }

            return;
        }

        ClipFileNamePreview = ClipFileNaming.BuildFileName("Counter Strike 2", timestamp, "mp4", SelectedClipFileNameScheme, Settings.CustomClipFileNameTemplate, "Counter Strike 2");
        ClipFileNameTemplateError = string.Empty;
    }

    public void SaveSelectedClipEditState()
    {
        if (string.IsNullOrWhiteSpace(SelectedVideoPath)) return;
        ClipEditSidecar.Save(Settings.LibraryFolder, SelectedVideoPath, new ClipEditSettings
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

    // One-time cleanup: session recordings used to title as "{game} Full
    // Session"; the convention is now "Session - {game}" (matching their
    // filenames). Rewrites old sidecars so existing tiles read the same as
    // new ones. Cheap - VODs only, skips anything already migrated.
    private void MigrateLegacySessionTitles()
    {
        try
        {
            var vodsRoot = LibraryLayout.VodsRoot(Settings.LibraryFolder);
            if (!Directory.Exists(vodsRoot)) return;
            foreach (var path in Directory.EnumerateFiles(vodsRoot, "*.*", SearchOption.AllDirectories).Where(MediaProbeService.IsVideoFile))
            {
                var info = ClipInfoSidecar.Load(Settings.LibraryFolder, path);
                if (info?.FileTitle is not { } title || !title.EndsWith(" Full Session", StringComparison.OrdinalIgnoreCase)) continue;
                var game = title[..^" Full Session".Length].Trim();
                if (string.IsNullOrWhiteSpace(game)) game = info.GameDisplayName ?? "Session";
                ClipInfoSidecar.Save(Settings.LibraryFolder, path, info with { FileTitle = $"Session - {game}" });
                AppLog.Info($"Session title migrated: {path} -> \"Session - {game}\".");
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Legacy session title migration failed", error);
        }
    }

    public async Task RefreshLibraryAsync()
    {
        var scanClock = System.Diagnostics.Stopwatch.StartNew();
        AllClips.Clear();
        ClearSelection();

        if (string.IsNullOrWhiteSpace(Settings.LibraryFolder) || !Directory.Exists(Settings.LibraryFolder))
        {
            StartLibraryWatcher();
            NotifyLibraryChrome();
            return;
        }

        LibraryLayout.EnsureRoots(Settings.LibraryFolder);
        if (Settings.LibraryLayoutVersion < LibraryLayout.CurrentVersion)
        {
            await MigrateLibraryLayoutAsync();
        }

        MigrateLegacySessionTitles();
        StartLibraryWatcher();

        var clips = _mediaProbe.EnumerateVideos(Settings.LibraryFolder)
            .Select(file => new ClipCardViewModel(_mediaProbe.CreateLibraryStub(file), Settings.LibraryFolder))
            .OrderByDescending(clip => clip.CreatedAt)
            .ToArray();

        foreach (var clip in clips) AllClips.Add(clip);

        NotifyLibraryChrome();
        StartLibraryHydration(clips);
        AppLog.Info($"Library refresh: {clips.Length} clips in {scanClock.ElapsedMilliseconds}ms.");
    }

    private async Task MigrateLibraryLayoutAsync()
    {
        if (!await _libraryLayoutMigrationLock.WaitAsync(0)) return;
        try
        {
            var libraryRoot = Settings.LibraryFolder;
            var paths = Directory.EnumerateFiles(libraryRoot, "*.*", SearchOption.AllDirectories)
                .Where(MediaProbeService.IsVideoFile)
                .ToArray();
            var moved = 0;
            var failed = 0;
            foreach (var sourcePath in paths)
            {
                try
                {
                    var duration = await _mediaProbe.GetDurationAsync(sourcePath);
                    if (duration <= TimeSpan.Zero) throw new InvalidOperationException("Could not read the video duration.");
                    var info = ClipInfoSidecar.Load(libraryRoot, sourcePath);
                    var (game, inferredGame) = ResolveLibraryGame(sourcePath, info);
                    var title = ResolveLibraryTitle(sourcePath, info, game, inferredGame);
                    var timestamp = info?.CapturedAt?.LocalDateTime ?? File.GetCreationTime(sourcePath);
                    var destinationDir = LibraryLayout.VideoDirectory(libraryRoot, duration, game);
                    Directory.CreateDirectory(destinationDir);
                    var fileName = ClipFileNaming.BuildFileName(title, timestamp, Path.GetExtension(sourcePath), Settings.ClipFileNameScheme, Settings.CustomClipFileNameTemplate, game);
                    var desiredPath = Path.Combine(destinationDir, fileName);
                    var destinationPath = string.Equals(sourcePath, desiredPath, StringComparison.OrdinalIgnoreCase)
                        ? sourcePath
                        : ClipFileNaming.BuildUniquePath(destinationDir, fileName);

                    if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(sourcePath, destinationPath);
                        MoveClipSidecars(sourcePath, destinationPath);
                        _mediaProbe.DeleteCacheFor(sourcePath);
                        if (Settings.ClipEdits.Remove(ClipEditKey(sourcePath), out var edit)) Settings.ClipEdits[ClipEditKey(destinationPath)] = edit;
                        moved++;
                    }
                    else
                    {
                        LibraryLayout.MoveSidecars(libraryRoot, sourcePath, destinationPath);
                    }

                    var medalKey = info?.MedalImportKey;
                    if (string.IsNullOrWhiteSpace(medalKey) && MedalImportService.TryResolveGameFromFileName(Path.GetFileNameWithoutExtension(sourcePath), out _, out _))
                    {
                        medalKey = MedalImportService.GetImportKey(timestamp.ToUniversalTime(), new FileInfo(destinationPath).Length);
                    }
                    ClipInfoSidecar.Save(libraryRoot, destinationPath, new ClipInfo(game, info?.AutoClipEventType, title, timestamp, medalKey));
                }
                catch (Exception error)
                {
                    AppLog.Error($"Library layout migration: failed moving {sourcePath}", error);
                    failed++;
                }
            }

            if (failed == 0)
            {
                Settings.LibraryLayoutVersion = LibraryLayout.CurrentVersion;
                Settings.ClipsMigratedToGameFolders = true;
            }
            SaveSettings();
            AppLog.Info($"Library layout migration: moved={moved}, failed={failed}.");
        }
        finally
        {
            _libraryLayoutMigrationLock.Release();
        }
    }

    private static (string Game, bool Inferred) ResolveLibraryGame(string videoPath, ClipInfo? info)
    {
        if (!string.IsNullOrWhiteSpace(info?.GameDisplayName) && !MedalImportService.IsStructuralFolderName(info.GameDisplayName))
        {
            return (info.GameDisplayName, false);
        }

        var sourceName = info?.FileTitle ?? Path.GetFileNameWithoutExtension(videoPath);
        if (MedalImportService.TryResolveGameFromFileName(sourceName, out var inferredGame, out _)) return (inferredGame, true);

        var parent = Path.GetFileName(Path.GetDirectoryName(videoPath));
        if (!string.IsNullOrWhiteSpace(parent) &&
            !MedalImportService.IsStructuralFolderName(parent) &&
            !string.Equals(parent, "Clips", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parent, "VODs", StringComparison.OrdinalIgnoreCase))
        {
            return (parent, false);
        }

        return ("Unknown Game", false);
    }

    private static string ResolveLibraryTitle(string videoPath, ClipInfo? info, string game, bool inferredGame)
    {
        var title = info?.FileTitle ?? ClipFileNaming.StripTimestampSuffix(Path.GetFileNameWithoutExtension(videoPath));
        return inferredGame && title.Contains("MedalTV", StringComparison.OrdinalIgnoreCase) ? game : title;
    }

    // One-time reorganization for clips saved before per-game subfolders
    // existed: only files sitting directly in the library root (Medal imports
    // and Full Sessions already live in their own subfolders and are left
    // alone). Reuses ClipCardViewModel's own game-name resolution (auto-clip
    // sidecar's GameDisplayName, or the filename-parsed name otherwise) so the
    // destination folder always matches what the game filter dropdown groups
    // by. Sidecars (edit state, clip info, paused ranges) move along with the
    // video; a name collision at the destination just leaves that one file
    // where it was instead of overwriting anything.
    private void MigrateFlatClipsIntoGameFolders()
    {
        var libraryFolder = Settings.LibraryFolder;
        if (string.IsNullOrWhiteSpace(libraryFolder) || !Directory.Exists(libraryFolder)) return;

        string[] topLevelVideos;
        try
        {
            topLevelVideos = Directory.EnumerateFiles(libraryFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(MediaProbeService.IsVideoFile)
                .ToArray();
        }
        catch (Exception error)
        {
            AppLog.Error("Clip game-folder migration: failed listing library folder.", error);
            return;
        }

        var moved = 0;
        foreach (var videoPath in topLevelVideos)
        {
            try
            {
                var card = new ClipCardViewModel(_mediaProbe.CreateLibraryStub(videoPath), Settings.LibraryFolder);
                var gameFolderName = ClipFileNaming.BuildBaseName(card.GameFilterKey);
                if (string.IsNullOrWhiteSpace(gameFolderName)) continue;

                var destinationDir = Path.Combine(libraryFolder, gameFolderName);
                var destinationPath = Path.Combine(destinationDir, Path.GetFileName(videoPath));
                if (File.Exists(destinationPath)) continue;

                Directory.CreateDirectory(destinationDir);
                File.Move(videoPath, destinationPath);
                MoveClipSidecars(videoPath, destinationPath);
                moved++;
            }
            catch (Exception error)
            {
                AppLog.Error($"Clip game-folder migration: failed moving {videoPath}", error);
            }
        }

        if (moved > 0) AppLog.Info($"Clip game-folder migration: moved {moved} clip(s) into per-game folders.");
    }

    private void MoveClipSidecars(string oldVideoPath, string newVideoPath)
    {
        LibraryLayout.MoveSidecars(Settings.LibraryFolder, oldVideoPath, newVideoPath);
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
            var clip = new ClipCardViewModel(media, Settings.LibraryFolder);
            var insertIndex = 0;
            while (insertIndex < AllClips.Count && AllClips[insertIndex].CreatedAt > clip.CreatedAt) insertIndex++;
            AllClips.Insert(insertIndex, clip);
        }

        NotifyLibraryChrome();
        AppLog.Debug($"Library quick add: {filePath} in {clock.ElapsedMilliseconds}ms.");
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
        UpdateSelectionOrder(clip, selected);
        UpdateDaySelectionStates();
        NotifySelectionChrome();
    }

    // Selects/deselects every clip sharing clip's date, not just clip itself -
    // the per-card date-header checkbox's job (replaces the old shared
    // per-day group header's select-all checkbox).
    public void ToggleDaySelection(ClipCardViewModel clip, bool selected)
    {
        var date = clip.CreatedAt.ToLocalTime().Date;
        foreach (var sibling in AllClips.Where(c => c.CreatedAt.ToLocalTime().Date == date))
        {
            sibling.IsSelected = selected;
            if (selected) _selectedPaths.Add(sibling.Path);
            else _selectedPaths.Remove(sibling.Path);
            UpdateSelectionOrder(sibling, selected);
        }

        UpdateDaySelectionStates();
        NotifySelectionChrome();
    }

    // Tracked separately from _selectedPaths (a HashSet, so insertion order isn't
    // guaranteed) so the card overlay can show "you selected this one 2nd" the way
    // GG's clip picker does, instead of just a plain checkmark.
    private readonly List<string> _selectionOrder = new();

    private void UpdateSelectionOrder(ClipCardViewModel clip, bool selected)
    {
        if (selected)
        {
            if (!_selectionOrder.Any(path => string.Equals(path, clip.Path, StringComparison.OrdinalIgnoreCase))) _selectionOrder.Add(clip.Path);
        }
        else
        {
            _selectionOrder.RemoveAll(path => string.Equals(path, clip.Path, StringComparison.OrdinalIgnoreCase));
        }

        for (var i = 0; i < _selectionOrder.Count; i++)
        {
            var index = i;
            var match = AllClips.FirstOrDefault(c => string.Equals(c.Path, _selectionOrder[index], StringComparison.OrdinalIgnoreCase));
            if (match is not null) match.SelectionOrder = index + 1;
        }

        if (!selected) clip.SelectionOrder = 0;
    }

    public async Task<int> DeleteSelectedAsync()
    {
        var selected = AllClips.Where(clip => clip.IsSelected).ToArray();
        foreach (var clip in selected)
        {
            File.Delete(clip.Path);
            _mediaProbe.DeleteCacheFor(clip.Path);
            ClipEditSidecar.Delete(Settings.LibraryFolder, clip.Path);
            ClipInfoSidecar.Delete(Settings.LibraryFolder, clip.Path);
            Settings.ClipEdits.Remove(ClipEditKey(clip.Path));
        }

        SaveSettings();
        await RefreshLibraryAsync();
        return selected.Length;
    }

    public async Task DeleteClipAsync(ClipCardViewModel clip)
    {
        File.Delete(clip.Path);
        _mediaProbe.DeleteCacheFor(clip.Path);
        ClipEditSidecar.Delete(Settings.LibraryFolder, clip.Path);
        ClipInfoSidecar.Delete(Settings.LibraryFolder, clip.Path);
        Settings.ClipEdits.Remove(ClipEditKey(clip.Path));

        SaveSettings();
        await RefreshLibraryAsync();
    }

    public async Task RenameClipAsync(ClipCardViewModel clip, string newTitle)
    {
        var sanitizedTitle = newTitle;
        foreach (var invalid in Path.GetInvalidFileNameChars()) sanitizedTitle = sanitizedTitle.Replace(invalid, '-');
        sanitizedTitle = sanitizedTitle.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(sanitizedTitle)) return;

        var oldPath = clip.Path;
        var oldStem = Path.GetFileNameWithoutExtension(oldPath);
        var strippedOld = ClipFileNaming.StripTimestampSuffix(oldStem);
        var suffix = oldStem[strippedOld.Length..];
        var newStem = sanitizedTitle + suffix;
        var directory = Path.GetDirectoryName(oldPath) ?? Settings.LibraryFolder;
        var newPath = ClipFileNaming.BuildUniquePath(directory, newStem + Path.GetExtension(oldPath));

        var existingInfo = ClipInfoSidecar.Load(Settings.LibraryFolder, oldPath);
        var updatedInfo = existingInfo is null
            ? new ClipInfo(null, null, sanitizedTitle)
            : existingInfo with { FileTitle = sanitizedTitle };
        ClipInfoSidecar.Save(Settings.LibraryFolder, oldPath, updatedInfo);

        File.Move(oldPath, newPath);
        MoveClipSidecars(oldPath, newPath);
        _mediaProbe.DeleteCacheFor(oldPath);

        await RefreshLibraryAsync();
    }

    // Called by the View once it's finished re-encoding the trimmed range over
    // the original file (see MainWindow.axaml.cs's SaveTrimToOriginalAsync) -
    // the trim sidecar is deleted rather than reset to 0/Duration because the
    // file on disk now IS exactly the trimmed range, so there's nothing left
    // to trim away; leaving stale TrimStart/TrimEndSeconds from the old, longer
    // duration around would just re-trim the already-trimmed file next open.
    public async Task FinalizeSavedTrimAsync(string path)
    {
        _mediaProbe.DeleteCacheFor(path);
        ClipEditSidecar.Delete(Settings.LibraryFolder, path);
        await AddOrUpdateLibraryClipAsync(path);
    }

    // Renames only the card's own display label (shown in place of "Clip from
    // {date}" for non-auto-clip cards) - unlike RenameClipAsync above, this
    // never touches the file on disk or FileTitle/GameDisplayName, so it can't
    // clobber the game association or a Medal import's original event title
    // (e.g. "4K - Inferno"). An empty title clears it back to "Clip from {date}".
    public async Task RenameClipTitleAsync(ClipCardViewModel clip, string newCustomTitle)
    {
        var sanitized = newCustomTitle.Trim();
        var existingInfo = ClipInfoSidecar.Load(Settings.LibraryFolder, clip.Path);
        var updatedInfo = (existingInfo ?? new ClipInfo(null, null)) with
        {
            CustomTitle = string.IsNullOrWhiteSpace(sanitized) ? null : sanitized
        };
        ClipInfoSidecar.Save(Settings.LibraryFolder, clip.Path, updatedInfo);

        await RefreshLibraryAsync();
    }

    public void CloseEditor()
    {
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;
        IsPlaying = false;
        IsEditorVisible = false;
        IsVideoFullscreen = false;
        SelectedCaptureBackend = string.Empty;

        // Trim edits are saved live to the sidecar as the user drags the trim
        // handles (SaveClipEditState), but the library card's pencil icon/trimmed
        // duration only reads that sidecar via ClipCardViewModel.UpdateMedia - so
        // without this, they'd stay stale until the next full library refresh
        // (app restart or manual Refresh). Posted instead of run inline so this
        // (and its ffprobe re-hydrate) happens after the close transition has
        // already rendered, instead of stalling it.
        var editedClipPath = SelectedVideoPath;
        if (!string.IsNullOrWhiteSpace(editedClipPath))
        {
            Dispatcher.UIThread.Post(() => _ = AddOrUpdateLibraryClipAsync(editedClipPath));
        }
    }

    public void OpenSettings()
    {
        _wasEditorVisibleBeforeSettings = IsEditorVisible;
        IsEditorVisible = false;
        IsSettingsVisible = true;
        SelectedSettingsSection = "Replay Buffer";
    }

    private static readonly string[] OnboardingStepOrder =
    {
        "Replay Buffer",
        "Capture Backend",
        "Startup",
        "Audio",
        "Game Audio Exclusions"
    };

    public bool IsOnboardingVisible
    {
        get => _isOnboardingVisible;
        set => SetProperty(ref _isOnboardingVisible, value);
    }

    public string OnboardingStep
    {
        get => _onboardingStep;
        set
        {
            if (!SetProperty(ref _onboardingStep, value)) return;
            OnPropertyChanged(nameof(OnboardingStepNumber));
            OnPropertyChanged(nameof(OnboardingBackEnabled));
            OnPropertyChanged(nameof(OnboardingNextLabel));
        }
    }

    public int OnboardingStepNumber => Array.IndexOf(OnboardingStepOrder, OnboardingStep) + 1;
    public int OnboardingStepCount => OnboardingStepOrder.Length;
    public bool OnboardingBackEnabled => OnboardingStepNumber > 1;
    public string OnboardingNextLabel => OnboardingStepNumber == OnboardingStepCount ? "Finish" : "Next";

    public void StartOnboarding()
    {
        OnboardingStep = OnboardingStepOrder[0];
        IsOnboardingVisible = true;
    }

    public void OnboardingBack()
    {
        var i = Array.IndexOf(OnboardingStepOrder, OnboardingStep);
        if (i > 0) OnboardingStep = OnboardingStepOrder[i - 1];
    }

    public void OnboardingNext()
    {
        var i = Array.IndexOf(OnboardingStepOrder, OnboardingStep);
        if (i == OnboardingStepOrder.Length - 1)
        {
            FinishOnboarding();
            return;
        }

        OnboardingStep = OnboardingStepOrder[i + 1];
    }

    public void FinishOnboarding()
    {
        IsOnboardingVisible = false;
        Settings.HasSeenOnboarding = true;
        SaveSettings();
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

    // Settings > Game Detection's "excluded from detection" list - mirrors
    // Settings.IgnoredGameExecutables so removals from the settings page and
    // additions from the header's detected-game flyout stay in sync.
    public ObservableCollection<string> IgnoredGameExecutableRows { get; } = new();

    public void SyncIgnoredGameExecutableRows()
    {
        IgnoredGameExecutableRows.Clear();
        foreach (var exe in Settings.IgnoredGameExecutables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            IgnoredGameExecutableRows.Add(exe);
        }
        OnPropertyChanged(nameof(HasIgnoredGameExecutables));
    }

    public bool HasIgnoredGameExecutables => Settings.IgnoredGameExecutables.Count > 0;

    public void AddIgnoredGameExecutable(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName)) return;
        if (Settings.IgnoredGameExecutables.Contains(executableName, StringComparer.OrdinalIgnoreCase)) return;
        Settings.IgnoredGameExecutables.Add(executableName);
        SaveSettings();
        SyncIgnoredGameExecutableRows();
        GameCatalogChanged?.Invoke(this, EventArgs.Empty);
        AppLog.Info($"Game detection: user excluded {executableName}.");
    }

    public void RemoveIgnoredGameExecutable(string executableName)
    {
        if (Settings.IgnoredGameExecutables.RemoveAll(name => string.Equals(name, executableName, StringComparison.OrdinalIgnoreCase)) == 0) return;
        SaveSettings();
        SyncIgnoredGameExecutableRows();
        GameCatalogChanged?.Invoke(this, EventArgs.Empty);
        AppLog.Info($"Game detection: user un-excluded {executableName}.");
    }

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

    public void AddGameFromProcess()
    {
        if (SelectedGameProcess is not { Name.Length: > 0 } process) return;
        NewCustomGameExecutable = process.Name;
        NewCustomGameDisplayName = string.IsNullOrWhiteSpace(process.WindowTitle)
            ? Path.GetFileNameWithoutExtension(process.Name)
            : process.WindowTitle;
        AddCustomGame();
        GameCandidateProcesses.Remove(process);
        SelectedGameProcess = null;
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

    // Toggles IsVisible per row instead of adding/removing rows from a separate
    // bound collection - GameCaptureRows itself is always the ItemsControl's
    // source now, so every row's container (and its Capture Backend ComboBox) is
    // realized exactly once and never torn down/recreated by the search box.
    private void ApplyGameSearchFilter()
    {
        var query = GameSearchText.Trim();
        foreach (var row in GameCaptureRows)
        {
            row.IsVisible = string.IsNullOrWhiteSpace(query) ||
                row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.ExecutableName.Contains(query, StringComparison.OrdinalIgnoreCase);
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

        // Refresh display names for already-configured microphones (a device's
        // friendly name can change enumeration-to-enumeration) without dropping
        // ones that are temporarily missing (same "don't lose a real choice over a
        // transient re-enumeration" reasoning as above) - keep the prior entry as-is
        // if it's not in this pass.
        for (var i = 0; i < SelectedMicrophones.Count; i++)
        {
            var current = SelectedMicrophones[i];
            var refreshed = MicrophoneDevices.FirstOrDefault(device => device.Id == current.Id);
            if (refreshed is not null && refreshed.Name != current.Name) SelectedMicrophones[i] = refreshed;
        }

        foreach (var id in Settings.MicrophoneDeviceIds)
        {
            if (SelectedMicrophones.Any(device => device.Id == id)) continue;
            var match = MicrophoneDevices.FirstOrDefault(device => device.Id == id);
            SelectedMicrophones.Add(match ?? new AudioDeviceOption(id, id));
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

        var selectedGameName = SelectedGameProcess?.Name;
        GameCandidateProcesses.Clear();
        foreach (var process in OpenProcesses.Where(IsGameCandidate))
        {
            GameCandidateProcesses.Add(process);
        }

        SelectedGameProcess = GameCandidateProcesses.FirstOrDefault(process => string.Equals(process.Name, selectedGameName, StringComparison.OrdinalIgnoreCase));
    }

    // Common non-game apps that legitimately keep a visible titled window open
    // (so ProcessListService's own filtering doesn't catch them) but that
    // nobody is adding as a "game" from this picker.
    private static readonly HashSet<string> NonGameExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.exe", "discordcanary.exe", "discordptb.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "zen.exe", "vivaldi.exe",
        "spotify.exe", "slack.exe", "teams.exe", "zoom.exe", "telegram.exe", "whatsapp.exe",
        "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "battle.net.exe",
        "origin.exe", "eaapp.exe", "eadesktop.exe", "ubisoftconnect.exe", "upc.exe", "galaxyclient.exe",
        "obs64.exe", "obs32.exe", "eve.exe", "code.exe", "notion.exe"
    };

    private bool IsGameCandidate(ProcessOption process)
    {
        if (NonGameExecutables.Contains(process.Name)) return false;
        if (GameCatalog.BuiltIn.ContainsKey(process.Name)) return false;
        if (Settings.GameCaptureOverrides.Any(g => string.Equals(g.ExecutableName, process.Name, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    public ReplayBufferConfig CreateReplayConfig()
    {
        var gameOverride = Settings.GameCaptureOverrides
            .FirstOrDefault(g => string.Equals(g.ExecutableName, ActiveGameDetection.ExeName, StringComparison.OrdinalIgnoreCase));
        var effectiveBackend = !string.IsNullOrWhiteSpace(gameOverride?.CaptureBackend) &&
                                !string.Equals(gameOverride.CaptureBackend, "Auto", StringComparison.OrdinalIgnoreCase)
            ? gameOverride.CaptureBackend
            : Settings.ReplayBackend;

        // SelectedChatProcess/SelectedMicrophoneDevice reflect whatever the
        // ComboBox last resolved to, and can legitimately be transiently null
        // (e.g. mid-refresh) even though a real choice is persisted in
        // Settings. CreateReplayConfig is called fresh on every single clip
        // save (not just once at buffer start), so a transient null here
        // silently dropped the mic/chat track from that one clip instead of
        // falling back to the last known-good persisted choice.
        var chatAudioProcessName = SelectedChatProcess?.Name;
        if (string.IsNullOrWhiteSpace(chatAudioProcessName)) chatAudioProcessName = Settings.ChatAudioProcessName;
        var chatAudioProcessNames = Settings.MultiChatAppEnabled
            ? ChatAudioApps.ToArray()
            : (string.IsNullOrWhiteSpace(chatAudioProcessName) ? Array.Empty<string>() : new[] { chatAudioProcessName });

        var microphoneDeviceId = SelectedMicrophoneDevice?.Id;
        if (string.IsNullOrWhiteSpace(microphoneDeviceId)) microphoneDeviceId = Settings.MicrophoneDeviceId;
        var microphoneDeviceIds = Settings.MultiMicrophoneEnabled
            ? SelectedMicrophones.Select(device => device.Id).ToArray()
            : (string.IsNullOrWhiteSpace(microphoneDeviceId) ? Array.Empty<string>() : new[] { microphoneDeviceId });
        var microphoneDeviceName = Settings.MultiMicrophoneEnabled
            ? SelectedMicrophones.FirstOrDefault()?.Name ?? string.Empty
            : SelectedMicrophoneDevice?.Name ?? string.Empty;

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
            chatAudioProcessNames,
            microphoneDeviceIds,
            microphoneDeviceName,
            Settings.GameAudioExcludedProcesses.ToArray(),
            ActiveGameDetection.DisplayName,
            ActiveGameDetection.ExeName,
            ActiveGameDetection.WindowTitle,
            ActiveGameDetection.WindowClass,
            effectiveBackend,
            GameWindowHandle: ActiveGameDetection.WindowHandle,
            MicrophoneNoiseSuppressionEnabled: Settings.MicrophoneNoiseSuppressionEnabled,
            MicrophoneNoiseSuppressionStrength: Settings.MicrophoneNoiseSuppressionStrength,
            FullSessionRecordingEnabled: Settings.FullSessionRecordingEnabled,
            FullSessionRecordingFolder: LibraryLayout.VodDirectory(Settings.LibraryFolder, ActiveGameDetection.DisplayName),
            FullSessionVideoCodec: Settings.FullSessionVideoCodec,
            FullSessionQuotaGb: Settings.FullSessionQuotaGb,
            FullSessionBackgroundFinalize: Settings.FullSessionBackgroundFinalize,
            AudioSyncOffsetMs: Settings.AudioSyncOffsetMs,
            ClipFileNameScheme: Settings.ClipFileNameScheme,
            CustomClipFileNameTemplate: Settings.CustomClipFileNameTemplate,
            LibraryFolder: Settings.LibraryFolder);
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

    // The actual length of what BuildExportArguments will encode - used by the
    // export progress popup to turn ffmpeg's "out_time" into a percentage.
    public TimeSpan ExportDuration
    {
        get
        {
            var end = TrimEnd > TrimStart ? TrimEnd : Duration;
            return TimeSpan.FromSeconds(Math.Max(0.1, (end - TrimStart).TotalSeconds));
        }
    }

    // Save Trim's variant of BuildExportArguments: keeps every audio stream
    // discrete (Game Audio / Chat Audio / Microphone, titles included) instead
    // of mixing them down to one track. Export mixes deliberately - most
    // players/upload targets only play a multi-track file's first audio
    // stream - but Save Trim replaces the clip itself, which must stay fully
    // editable afterward: mixing here would permanently destroy the per-track
    // mute/volume control the editor is built around. Volumes aren't baked in
    // either, for the same reason.
    public IReadOnlyList<string> BuildTrimArguments(string outputPath, bool useHardwareEncoder = true)
    {
        var startSeconds = Math.Max(0, TrimStart.TotalSeconds);
        var end = TrimEnd > TrimStart ? TrimEnd : Duration;
        var durationSeconds = Math.Max(0.1, (end - TrimStart).TotalSeconds);
        var args = new List<string>
        {
            "-y",
            "-progress", "pipe:1",
            "-stats_period", "0.1",
            "-nostats",
            "-ss", startSeconds.ToString("0.###"),
            "-t", durationSeconds.ToString("0.###"),
            "-i", SelectedVideoPath,
            "-map", "0:v:0?",
            "-map", "0:a?",
            "-sn",
            // Stream titles ("Game Audio" etc.) ride along with the mapped
            // streams by default; this keeps container-level metadata too.
            "-map_metadata", "0"
        };
        args.AddRange(BuildExportCodecArguments(useHardwareEncoder));
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(outputPath);
        return args;
    }

    public IReadOnlyList<string> BuildExportArguments(string outputPath, bool useHardwareEncoder = true)
    {
        var startSeconds = Math.Max(0, TrimStart.TotalSeconds);
        var end = TrimEnd > TrimStart ? TrimEnd : Duration;
        var durationSeconds = Math.Max(0.1, (end - TrimStart).TotalSeconds);
        var args = new List<string>
        {
            "-y",
            // Machine-readable progress lines on stdout (key=value, one per
            // encoded frame/chunk) - lets the export progress popup show a real
            // percentage instead of just spinning indefinitely. stats_period
            // drops ffmpeg's default 0.5s reporting interval to 100ms so the
            // bar moves smoothly instead of jumping forward twice a second.
            "-progress", "pipe:1",
            "-stats_period", "0.1",
            "-nostats",
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
            args.Add($"volume={VolumeMultiplier(audioTracks[0].EffectiveVolumePercent):0.###}");
        }
        else if (audioTracks.Length > 1)
        {
            var filter = new System.Text.StringBuilder();
            var labels = new List<string>();
            foreach (var track in audioTracks)
            {
                var label = $"a{track.StreamIndex}";
                filter.Append($"[0:{track.StreamIndex}]volume={VolumeMultiplier(track.EffectiveVolumePercent):0.###}[{label}];");
                labels.Add($"[{label}]");
            }

            filter.Append($"{string.Join(string.Empty, labels)}amix=inputs={audioTracks.Length}:normalize=0[aout]");
            args.Add("-filter_complex");
            args.Add(filter.ToString());
            args.Add("-map");
            args.Add("[aout]");
        }

        args.AddRange(BuildExportCodecArguments(useHardwareEncoder));
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

    // NVENC first: the CPU encoders here (libx265, and especially libaom-av1)
    // took minutes for clips NVENC finishes in seconds, and this app already
    // targets NVENC hardware for capture. Callers retry with
    // useHardwareEncoder: false when ffmpeg fails - which is exactly what
    // happens on a machine with no NVIDIA GPU - so the CPU path is the
    // fallback, not a separate user-facing choice.
    private IReadOnlyList<string> BuildExportCodecArguments(bool useHardwareEncoder)
    {
        if (useHardwareEncoder)
        {
            return SelectedExportCodec?.Label switch
            {
                "H.265" => new[] { "-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "24", "-b:v", "0" },
                "AV1" => new[] { "-c:v", "av1_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "32", "-b:v", "0" },
                _ => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "20", "-b:v", "0" }
            };
        }

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
        // Set here, synchronously, so the thumbnail placeholder is already
        // showing by the moment IsEditorVisible flips true below - the actual
        // video load/decode is deferred a tick later (QueueEditorPlayback),
        // and without this the editor would briefly show an empty/black
        // VideoView in between.
        IsEditorVideoLoading = true;
        if (!preserveEditorText)
        {
            EditorTitle = media.Name;
            EditorDescription = string.Empty;
        }
        SelectedCreatedAtLocal = media.CreatedAt.ToLocalTime().DateTime;
        SelectedCreated = $"Created: {SelectedCreatedAtLocal:d MMM yyyy, H:mm}";
        SelectedQuality = media.Height > 0
            ? $"Video Quality: {ResolutionLabel(media.Height)}"
            : "Video Quality: Unknown";
        SelectedSize = $"Size: {FormatBytes(media.SizeBytes)}";
        var isMedalImport = !string.IsNullOrWhiteSpace(ClipInfoSidecar.Load(Settings.LibraryFolder, media.Path)?.MedalImportKey);
        SelectedCaptureBackend = isMedalImport
            ? "Imported from Medal"
            : (string.IsNullOrWhiteSpace(media.CaptureBackend) ? string.Empty : $"Captured with: {media.CaptureBackend}");
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
        var edit = ClipEditSidecar.Load(Settings.LibraryFolder, path);
        if (edit is null)
        {
            if (!Settings.ClipEdits.TryGetValue(ClipEditKey(path), out edit)) return;
            // Migrate this clip's edit state out of settings.json and into its own
            // sidecar file the first time it's opened after upgrading.
            ClipEditSidecar.Save(Settings.LibraryFolder, path, edit);
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
        foreach (var clip in AllClips) clip.SelectionOrder = 0;
        _selectionOrder.Clear();
        NotifySelectionChrome();
    }

    // Recomputes each card's IsDaySelected (true only when every clip
    // sharing its date is currently selected) - drives the checked state of
    // the per-card date-header checkbox.
    private void UpdateDaySelectionStates()
    {
        foreach (var dayGroup in AllClips.GroupBy(clip => clip.CreatedAt.ToLocalTime().Date))
        {
            var allSelected = dayGroup.All(clip => clip.IsSelected);
            foreach (var clip in dayGroup) clip.IsDaySelected = allSelected;
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
        RecomputeGameFilterBadges();
        UpdateFirstOfDateFlags();
    }

    // AllClips is always sorted newest-first, so the first clip encountered
    // per distinct date is the one the date header should render on -
    // matches where the old shared per-day group header used to sit (the
    // top of that day's clips).
    private void UpdateFirstOfDateFlags()
    {
        var seenDates = new HashSet<DateTime>();
        foreach (var clip in AllClips)
        {
            clip.IsFirstOfDate = seenDates.Add(clip.CreatedAt.ToLocalTime().Date);
        }
    }

    private readonly HashSet<string> _activeGameFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeClipTypeFilters = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<FilterOptionViewModel> GameFilterOptions { get; } = new();
    public ObservableCollection<FilterOptionViewModel> ClipTypeFilterOptions { get; } = new();

    public bool IsGameFilterActive => _activeGameFilters.Count > 0;
    public string ActiveGameFilterLabel => string.Join(", ", _activeGameFilters);
    public bool IsClipTypeFilterActive => _activeClipTypeFilters.Count > 0;

    private const string ClipTypeManual = "Manual";
    private const string ClipTypeAutoClip = "AutoClip";
    private const string ClipTypeVod = "Vod";
    private const string ClipTypeMedalImport = "MedalImport";

    // Rebuilds the Game Filters / Clip Type Filters checklist option lists -
    // works the same for EVE-recorded and Medal-imported clips since both
    // resolve GameFilterKey (TileTopLabel) the same way. Re-run any time the
    // library's clip set changes, not just once.
    private void RecomputeGameFilterBadges()
    {
        var countsByGame = AllClips
            .GroupBy(clip => clip.GameFilterKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        // A previously-active game filter's target game can disappear
        // entirely (its last clip got deleted) - drop it from the active
        // set rather than leave the library showing zero clips with no
        // visible way to tell why.
        var removedAnyGameFilter = _activeGameFilters.RemoveWhere(name => !countsByGame.ContainsKey(name)) > 0;

        GameFilterOptions.Clear();
        foreach (var game in countsByGame.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            GameFilterOptions.Add(new FilterOptionViewModel(
                game.Key,
                $"{game.Key} ({game.Value})",
                _activeGameFilters.Contains(game.Key),
                OnGameFilterOptionChanged));
        }

        // Same "(count)" suffix the game filter rows above already get.
        var manualCount = AllClips.Count(clip => clip.IsManualClip);
        var autoClipCount = AllClips.Count(clip => clip.IsAutoClip);
        var vodCount = AllClips.Count(clip => clip.IsVod);
        var medalImportCount = AllClips.Count(clip => clip.IsMedalImport);
        var hasMedalImports = medalImportCount > 0;
        var removedAnyClipTypeFilter = !hasMedalImports && _activeClipTypeFilters.Remove(ClipTypeMedalImport);

        ClipTypeFilterOptions.Clear();
        ClipTypeFilterOptions.Add(new FilterOptionViewModel(ClipTypeManual, $"Manual clips ({manualCount})", _activeClipTypeFilters.Contains(ClipTypeManual), OnClipTypeFilterOptionChanged));
        ClipTypeFilterOptions.Add(new FilterOptionViewModel(ClipTypeAutoClip, $"Auto-Clips ({autoClipCount})", _activeClipTypeFilters.Contains(ClipTypeAutoClip), OnClipTypeFilterOptionChanged));
        ClipTypeFilterOptions.Add(new FilterOptionViewModel(ClipTypeVod, $"Full Session / VODs ({vodCount})", _activeClipTypeFilters.Contains(ClipTypeVod), OnClipTypeFilterOptionChanged));
        if (hasMedalImports)
        {
            ClipTypeFilterOptions.Add(new FilterOptionViewModel(ClipTypeMedalImport, $"Medal imports ({medalImportCount})", _activeClipTypeFilters.Contains(ClipTypeMedalImport), OnClipTypeFilterOptionChanged));
        }

        if (removedAnyGameFilter) ApplyGameFilters();
        if (removedAnyClipTypeFilter) ApplyClipTypeFilters();
        if (removedAnyGameFilter || removedAnyClipTypeFilter)
        {
            OnPropertyChanged(nameof(IsGameFilterActive));
            OnPropertyChanged(nameof(ActiveGameFilterLabel));
            OnPropertyChanged(nameof(IsClipTypeFilterActive));
        }
    }

    public void ClearGameFilters()
    {
        if (_activeGameFilters.Count == 0) return;
        _activeGameFilters.Clear();
        foreach (var option in GameFilterOptions) option.SetCheckedSilently(false);
        ApplyGameFilters();
        OnPropertyChanged(nameof(IsGameFilterActive));
        OnPropertyChanged(nameof(ActiveGameFilterLabel));
    }

    private void OnGameFilterOptionChanged(string gameName, bool isChecked)
    {
        if (isChecked) _activeGameFilters.Add(gameName);
        else _activeGameFilters.Remove(gameName);
        ApplyGameFilters();
        OnPropertyChanged(nameof(IsGameFilterActive));
        OnPropertyChanged(nameof(ActiveGameFilterLabel));
    }

    private void OnClipTypeFilterOptionChanged(string key, bool isChecked)
    {
        if (isChecked) _activeClipTypeFilters.Add(key);
        else _activeClipTypeFilters.Remove(key);
        ApplyClipTypeFilters();
        OnPropertyChanged(nameof(IsClipTypeFilterActive));
    }

    private void ApplyGameFilters()
    {
        foreach (var clip in AllClips)
        {
            clip.IsMatchedByGameFilter = _activeGameFilters.Count == 0 || _activeGameFilters.Contains(clip.GameFilterKey);
        }
    }

    private void ApplyClipTypeFilters()
    {
        foreach (var clip in AllClips)
        {
            clip.IsMatchedByClipTypeFilter = _activeClipTypeFilters.Count == 0 || MatchesClipTypeFilter(clip);
        }
    }

    private bool MatchesClipTypeFilter(ClipCardViewModel clip)
    {
        return (clip.IsManualClip && _activeClipTypeFilters.Contains(ClipTypeManual))
            || (clip.IsAutoClip && _activeClipTypeFilters.Contains(ClipTypeAutoClip))
            || (clip.IsVod && _activeClipTypeFilters.Contains(ClipTypeVod))
            || (clip.IsMedalImport && _activeClipTypeFilters.Contains(ClipTypeMedalImport));
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
            // On a network drive, waveform decoding competes with LibVLC's
            // video stream and the audio chunk extractor for the same remote
            // file the moment a clip opens - SMB seek thrash from three
            // concurrent readers is what made long network clips stutter in
            // the editor while standalone VLC played them fine. Give playback
            // a head start; the waveform is the least urgent of the three.
            if (PlaybackSession.IsNetworkPath(media.Path))
            {
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            }

            // Per-segment partial updates so long clips paint their waveform
            // progressively left-to-right instead of showing nothing until the
            // whole file has been decoded.
            void OnPartial(int streamIndex, IReadOnlyList<double> peaks) => Dispatcher.UIThread.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (!string.Equals(SelectedVideoPath, media.Path, StringComparison.OrdinalIgnoreCase)) return;
                var track = TimelineTracks.FirstOrDefault(track => track.IsAudio && track.StreamIndex == streamIndex);
                if (track is not null) track.WaveformPeaks = peaks;
            });

            var waveforms = await _mediaProbe.LoadWaveformsAsync(media, cancellationToken, OnPartial);
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
                // Guarded on IsEditorVisible too - AddOrUpdateLibraryClipAsync also
                // calls this after the editor closes (to refresh the library card),
                // and SelectedVideoPath still points at that clip at that point.
                // Without the guard, OpenMedia's unconditional IsEditorVisible = true
                // would pop the editor back open right after the user closed it.
                if (IsEditorVisible && string.Equals(SelectedVideoPath, clip.Path, StringComparison.OrdinalIgnoreCase))
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
