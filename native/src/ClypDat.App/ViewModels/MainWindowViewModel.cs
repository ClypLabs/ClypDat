using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly MediaProbeService _mediaProbe = new();
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _libraryHydrationCts;
    private CancellationTokenSource? _waveformCts;
    private FileSystemWatcher? _libraryWatcher;
    private DispatcherTimer? _libraryFolderRetryTimer;
    private readonly DispatcherTimer _libraryRefreshDebounce;
    private readonly DispatcherTimer _clipNotReadyMessageTimer;
    // See WasRecentlySelfAdded - suppresses the redundant full-library
    // refresh the folder watcher used to trigger for a clip
    // AddOrUpdateLibraryClipAsync had already added directly.
    private readonly Dictionary<string, DateTime> _recentlySelfAddedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SelfAddedSuppressWindow = TimeSpan.FromSeconds(5);
    // Paths that changed since the debounce last fired - see ScheduleLibraryRefresh
    // for why the suppression check now happens against THIS set at debounce-fire
    // time instead of at the moment each watcher event arrives.
    private readonly HashSet<string> _pendingLibraryChangePaths = new(StringComparer.OrdinalIgnoreCase);
    private int _libraryRefreshDebounceRetries;
    // Set by MainWindow around a replay save's mux/remux - a save can run
    // well past the debounce's own 650ms (the destination file often exists,
    // and fires the watcher's Created event, from near the start of a mux
    // that then keeps running for seconds more), so the debounce firing
    // before AddOrUpdateLibraryClipAsync's own self-add mark has landed
    // isn't necessarily an external change yet. See the debounce Tick
    // handler below.
    public bool IsSavingReplayClip { get; set; }
    private readonly SemaphoreSlim _libraryLayoutMigrationLock = new(1, 1);
    private readonly AudioDeviceService _audioDevices = new();
    private bool _isReplayRecording;
    private bool _isEditorVisible;
    private bool _isSettingsVisible;
    private string _selectedSettingsSection = "General";
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
    private string _autoClipSearchText = string.Empty;
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
    private bool _isHydratingLibrary;
    private int _hydrationTotal;
    private int _hydrationCompleted;
    private string _hydrationPhaseLabel = "Loading clip info";
    private string _hydrationEtaText = string.Empty;
    // _hydrationClock restarts at the beginning of EACH phase (see
    // RunHydrationPassAsync) so the ETA's rate always reflects whichever
    // pass is actually running right now, same idea as the export/save-trim
    // dialogs' own ETA. _hydrationOverallCompleted/Total track progress
    // across ALL three passes separately from HydrationCompleted/
    // HydrationTotal (which reset per-phase for the displayed fraction) -
    // the ETA projects the current phase's rate across however many items
    // are left in the WHOLE job, not just the current phase.
    private readonly System.Diagnostics.Stopwatch _hydrationClock = new();
    private int _hydrationOverallCompleted;
    private int _hydrationOverallTotal;
    private string _clipNotReadyMessage = string.Empty;
    private double _masterVolumePercent;
    private bool _isMasterMuted;
    private double _videoZoom = 1.0;
    private double _videoPanY;
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
        if (!string.IsNullOrWhiteSpace(Settings.LastSettingsSection)) _selectedSettingsSection = Settings.LastSettingsSection;
        // Curated game-icons.json entries (delisted store names, curated
        // Steam app IDs like the CS:GO fix) only reach a running app through
        // this - RequestMissingGameIcons only pulls it in as a side effect of
        // a game actually missing an icon, which could be never for someone
        // whose library is already fully resolved. Kicking it off here too
        // means every launch picks up curated-list edits on its own, not
        // just launches that happen to hit a missing icon. EnsureLoadedAsync
        // (not ForceRefreshAsync) still respects its own once-a-day window,
        // so this is cheap on every launch after the first that day.
        _ = Task.Run(() => RemoteGameIconsService.EnsureLoadedAsync());
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
            new("Auto (recommended)", "Auto", "Uses ClypDat's own capture engine for every game - no process hook, so it's safe for anti-cheat-protected games too, with no stop/start gaps between segments."),
            new("ClypDat", "Native", "ClypDat's own capture engine - a true rolling buffer with no stop/start gaps between segments, and true per-window capture that keeps recording the game through alt-tabs and overlays. Used automatically on Auto."),
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
        _masterVolumePercent = Settings.EditorMasterVolume;
        UpdateClipFileNamePreview();
        ExcludedProcesses = new ObservableCollection<string>(Settings.GameAudioExcludedProcesses);
        ChatAudioApps = new ObservableCollection<string>(Settings.ChatAudioProcessNames);
        SelectedMicrophones = new ObservableCollection<AudioDeviceOption>();
        GameCaptureRows = new ObservableCollection<GameBackendRowViewModel>();
        EnsureAutoClipSettings();
        AutoClipGames = new ObservableCollection<AutoClipGameViewModel>(AutoClipCatalog.Active.Select(definition =>
            new AutoClipGameViewModel(definition, Settings.AutoClipping.Games[definition.Id], SaveSettings)));
        ComingSoonAutoClipGames = new ObservableCollection<string>(AutoClipCatalog.ComingSoon);
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
            // Re-check suppression HERE, against whatever's still pending, not
            // back when each watcher event first arrived - a newly-saved
            // clip's own self-add mark (AddOrUpdateLibraryClipAsync) only
            // lands once its remux finishes, but the file (and so the
            // watcher's Created/Changed event) exists on disk well before
            // that, often before the mark does. Checking immediately at event
            // time raced that mark and lost often enough to trigger a full
            // RefreshLibraryAsync right on top of the incremental add - the
            // library-wide hydration fraction (e.g. "1/28") that briefly
            // flashed for what was really just one new clip. 650ms is enough
            // slack for even a slow remux's mark to have landed by the time
            // this actually fires; if every pending path turns out to have
            // been self-added by now, there's nothing left to refresh for.
            var pending = _pendingLibraryChangePaths.ToArray();
            if (pending.Length > 0 && pending.All(WasRecentlySelfAdded))
            {
                _pendingLibraryChangePaths.Clear();
                _libraryRefreshDebounceRetries = 0;
                return;
            }

            // A replay save's own remux can still be running well past 650ms
            // (its self-add mark only lands once the mux finishes, but the
            // destination file - and the watcher event for it - shows up on
            // disk from near the mux's start) - that's not yet evidence of
            // an external change, so keep waiting on the SAME pending set
            // instead of refreshing the whole library out from under a save
            // that's still in flight. Capped so a save that somehow never
            // clears the flag can't wait forever.
            if (IsSavingReplayClip && ++_libraryRefreshDebounceRetries < 60)
            {
                ScheduleLibraryRefresh();
                return;
            }

            _pendingLibraryChangePaths.Clear();
            _libraryRefreshDebounceRetries = 0;
            await RefreshLibraryAsync();
        };
        _clipNotReadyMessageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _clipNotReadyMessageTimer.Tick += (_, _) =>
        {
            _clipNotReadyMessageTimer.Stop();
            ClipNotReadyMessage = string.Empty;
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
    public ObservableCollection<AutoClipGameViewModel> AutoClipGames { get; }
    public ObservableCollection<string> ComingSoonAutoClipGames { get; }
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
    // Names whatever is actually on screen: the filtered-to game(s) and/or
    // clip type(s), falling back to "Clips" with nothing filtered. Games come
    // first because that's the coarser cut - "Fortnite - Auto-Clips" reads the
    // way the user got there (picked the game, then narrowed the type).
    public string LibraryTitle
    {
        get
        {
            var parts = new List<string>();
            if (_activeGameFilters.Count > 0) parts.Add(string.Join(", ", _activeGameFilters.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)));

            var types = ClipTypeFilterOptions
                .Where(option => _activeClipTypeFilters.Contains(option.Key))
                .Select(option => ClipTypeTitle(option.Key))
                .ToArray();
            if (types.Length > 0) parts.Add(string.Join(", ", types));

            // Count is of what's actually on screen, so it follows the filter
            // rather than always reporting the whole library.
            var shown = AllClips.Count(clip => clip.IsVisibleInLibrary);
            var name = parts.Count == 0 ? "Clips" : string.Join(" - ", parts);
            return $"{name} ({shown})";
        }
    }

    // The filter rows carry a count in their label ("Auto-Clips (3)"), which
    // has no business in a heading.
    private static string ClipTypeTitle(string key) => key switch
    {
        ClipTypeManual => "Manual Clips",
        ClipTypeAutoClip => "Auto-Clips",
        ClipTypeVod => "Full Session / VODs",
        ClipTypeMedalImport => "Medal Imports",
        _ => key
    };
    public string LibraryFolderDisplay => string.IsNullOrWhiteSpace(Settings.LibraryFolder)
        ? "Choose a folder"
        : Settings.LibraryFolder;

    public string LibraryLocationText => $"Location: {LibraryFolderDisplay}";
    private long LibraryUsedBytes => AllClips.Sum(clip => clip.SizeBytes);
    public string LibrarySizeDisplay => FormatBytes(LibraryUsedBytes);

    // Real disk stats for the library folder's drive, queried fresh each
    // access (cheap - a single DriveInfo lookup) rather than cached, since
    // free space drifts over time and this is only read when the storage
    // flyout is actually open. Network drives / a not-yet-chosen folder
    // just fall back to (0, 0) - HasDriveStats gates the flyout's content
    // on that instead of showing a nonsense "0 B free of 0 B".
    private (long Total, long Free) DriveStats
    {
        get
        {
            try
            {
                var folder = Settings.LibraryFolder;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return (0, 0);
                var drive = new DriveInfo(Path.GetPathRoot(folder) ?? folder);
                return (drive.TotalSize, drive.AvailableFreeSpace);
            }
            catch
            {
                return (0, 0);
            }
        }
    }

    public bool HasDriveStats => DriveStats.Total > 0;
    public string LibraryDriveFreeOfTotalDisplay => HasDriveStats
        ? $"{FormatBytes(DriveStats.Free)} free of {FormatBytes(DriveStats.Total)}"
        : "Unavailable";
    public string LibraryDriveUsedPercentDisplay => HasDriveStats
        ? $"{(int)Math.Round((DriveStats.Total - DriveStats.Free) * 100.0 / DriveStats.Total)}% used"
        : string.Empty;
    public double LibraryDriveUsedFraction => HasDriveStats
        ? Math.Clamp((DriveStats.Total - DriveStats.Free) / (double)DriveStats.Total, 0, 1)
        : 0;

    // ---- Library storage limit ----------------------------------------
    // Optional soft cap on the library's own size. With one set, the sidebar
    // ring fills against it (that's the number the user actually cares about
    // once they've chosen one); with no limit it falls back to showing whole-
    // drive usage so the ring is never just an empty outline.
    private const long BytesPerGb = 1_073_741_824;

    public sealed record StorageLimitOption(string Label, int Gb);

    public IReadOnlyList<StorageLimitOption> LibraryStorageLimitOptions { get; } = new StorageLimitOption[]
    {
        new("No limit", 0),
        new("25 GB", 25),
        new("50 GB", 50),
        new("100 GB", 100),
        new("250 GB", 250),
        new("500 GB", 500),
        new("1 TB", 1024),
        new("Custom", -1)
    };

    private bool _customLibraryStorageLimitSelected;

    public StorageLimitOption SelectedLibraryStorageLimit
    {
        get
        {
            if (IsCustomLibraryStorageLimit) return LibraryStorageLimitOptions[^1];
            return LibraryStorageLimitOptions.FirstOrDefault(option => option.Gb == Settings.LibraryStorageLimitGb) ?? LibraryStorageLimitOptions[0];
        }
        set
        {
            if (value.Gb < 0)
            {
                _customLibraryStorageLimitSelected = true;
                if (Settings.LibraryStorageLimitGb <= 0) Settings.LibraryStorageLimitGb = 100;
            }
            else
            {
                _customLibraryStorageLimitSelected = false;
                Settings.LibraryStorageLimitGb = value.Gb;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomLibraryStorageLimit));
            OnPropertyChanged(nameof(CustomLibraryStorageLimitGb));
            NotifyStorageChrome();
            SaveSettings();
        }
    }

    // Custom is active when explicitly picked, or when a saved value doesn't
    // match any preset (a previous custom number surviving a restart).
    public bool IsCustomLibraryStorageLimit =>
        _customLibraryStorageLimitSelected ||
        (Settings.LibraryStorageLimitGb > 0 && LibraryStorageLimitOptions.All(option => option.Gb != Settings.LibraryStorageLimitGb));

    public string CustomLibraryStorageLimitGb
    {
        get => Settings.LibraryStorageLimitGb > 0 ? Settings.LibraryStorageLimitGb.ToString() : string.Empty;
        set
        {
            if (!int.TryParse(value, out var gb)) return;
            Settings.LibraryStorageLimitGb = Math.Clamp(gb, 1, 1_000_000);
            OnPropertyChanged();
            NotifyStorageChrome();
            SaveSettings();
        }
    }

    public bool HasLibraryStorageLimit => Settings.LibraryStorageLimitGb > 0;
    private long LibraryStorageLimitBytes => Settings.LibraryStorageLimitGb * BytesPerGb;
    public bool IsOverLibraryStorageLimit => HasLibraryStorageLimit && LibraryUsedBytes > LibraryStorageLimitBytes;

    public double LibraryStorageLimitUsedFraction => HasLibraryStorageLimit
        ? Math.Clamp(LibraryUsedBytes / (double)LibraryStorageLimitBytes, 0, 1)
        : 0;

    // What the sidebar ring actually draws: the limit when there is one,
    // otherwise whole-drive usage.
    public double LibraryStorageRingFraction => HasLibraryStorageLimit ? LibraryStorageLimitUsedFraction : LibraryDriveUsedFraction;

    // Over-limit stays a fixed red - that's a warning, and it would stop
    // reading as one if it followed the user's accent. Under the limit uses
    // the app's accent brush, which itself tracks the Windows accent colour.
    public IBrush LibraryStorageRingBrush => IsOverLibraryStorageLimit
        ? new SolidColorBrush(Color.Parse("#E5484D"))
        : Application.Current?.Resources["AccentBrush"] as IBrush ?? new SolidColorBrush(Color.Parse("#5864E8"));

    public string LibraryStorageLimitSummary
    {
        get
        {
            if (!HasLibraryStorageLimit) return $"{FormatBytes(LibraryUsedBytes)} used - No Limit";
            var percent = (int)Math.Round(LibraryUsedBytes * 100.0 / LibraryStorageLimitBytes);
            return $"{FormatBytes(LibraryUsedBytes)} of {LibraryStorageLimitLabel} ({percent}%)";
        }
    }

    // Any limit past 1024 GB reads in TB, not just the round multiples that
    // divide exactly - a custom 1500 GB used to render as "1500 GB", wide
    // enough to run out of the rail it sits in. One decimal, and no trailing
    // ".0" on the round ones.
    private string LibraryStorageLimitLabel
    {
        get
        {
            var gb = Settings.LibraryStorageLimitGb;
            if (gb < 1024) return $"{gb} GB";
            var tb = gb / 1024d;
            return tb % 1 == 0 ? $"{tb:0} TB" : $"{tb:0.#} TB";
        }
    }

    // Shown stacked under the sidebar ring, always - has to fit the 64px
    // rail, so it's just "of <limit>" under the used figure, or "No Limit"
    // when there's none, so the current setting is readable without opening
    // the flyout (the used figure above it stays visible either way).
    public string LibraryStorageLimitShortDisplay => HasLibraryStorageLimit ? $"of {LibraryStorageLimitLabel}" : "No Limit";

    private void NotifyStorageChrome()
    {
        OnPropertyChanged(nameof(HasLibraryStorageLimit));
        OnPropertyChanged(nameof(IsOverLibraryStorageLimit));
        OnPropertyChanged(nameof(LibraryStorageLimitUsedFraction));
        OnPropertyChanged(nameof(LibraryStorageRingFraction));
        OnPropertyChanged(nameof(LibraryStorageRingBrush));
        OnPropertyChanged(nameof(LibraryStorageLimitSummary));
        OnPropertyChanged(nameof(LibraryStorageLimitShortDisplay));
    }
    public double LibraryUsedFractionOfDrive => HasDriveStats ? Math.Clamp(LibraryUsedBytes / (double)DriveStats.Total, 0, 1) : 0;
    public double LibraryOtherUsedFractionOfDrive => HasDriveStats
        ? Math.Clamp((DriveStats.Total - DriveStats.Free - LibraryUsedBytes) / (double)DriveStats.Total, 0, 1)
        : 0;
    public string LibraryClipsUsageDisplay => $"ClypDat clips: {FormatBytes(LibraryUsedBytes)} ({AllClips.Count} clips)";
    public string LibraryOtherUsageDisplay => HasDriveStats
        ? $"Rest of drive: {FormatBytes(Math.Max(0, DriveStats.Total - DriveStats.Free - LibraryUsedBytes))}"
        : string.Empty;
    // Rough "how many more clips could fit" estimate from this library's
    // own average clip size - meaningless with zero clips to average from.
    public string LibraryPossibleClipsDisplay
    {
        get
        {
            if (!HasDriveStats || AllClips.Count == 0) return string.Empty;
            var averageBytes = LibraryUsedBytes / (double)AllClips.Count;
            if (averageBytes <= 0) return string.Empty;
            var possible = (long)(DriveStats.Free / averageBytes);
            return $"Possible number of clips: ~{possible:N0}";
        }
    }

    public string HotkeyDisplay => IsCapturingHotkey ? "Press keys..." : Settings.SaveReplayHotkey;

    public int SelectedCount => _selectedPaths.Count;
    public bool HasSelection => SelectedCount > 0;
    public bool HasNoSelection => !HasSelection;
    public bool ShowLibraryActions => HasNoSelection && IsLibraryVisible;
    public bool ShowLibraryStatus => IsLibraryVisible;

    // Drives the "Building your library..." banner - HydrateLibraryClipsAsync
    // (one clip's ffprobe/thumbnail at a time, see its ParallelOptions) can
    // take a while on a large or network-drive library, and without this the
    // window otherwise looks idle/frozen while every card's duration is
    // still 0:00 and thumbnails are still filling in.
    public bool IsHydratingLibrary
    {
        get => _isHydratingLibrary;
        private set => SetProperty(ref _isHydratingLibrary, value);
    }

    public int HydrationTotal
    {
        get => _hydrationTotal;
        private set
        {
            if (!SetProperty(ref _hydrationTotal, value)) return;
            OnPropertyChanged(nameof(HydrationProgressText));
            OnPropertyChanged(nameof(HydrationProgressFraction));
        }
    }

    public int HydrationCompleted
    {
        get => _hydrationCompleted;
        private set
        {
            if (!SetProperty(ref _hydrationCompleted, value)) return;
            OnPropertyChanged(nameof(HydrationProgressText));
            OnPropertyChanged(nameof(HydrationProgressFraction));
        }
    }

    // Which of the three hydration passes is currently running (RunHydrationPassAsync) -
    // "Loading clip info" / "Loading thumbnails" / "Loading timelines".
    public string HydrationPhaseLabel
    {
        get => _hydrationPhaseLabel;
        private set
        {
            if (!SetProperty(ref _hydrationPhaseLabel, value)) return;
            OnPropertyChanged(nameof(HydrationProgressText));
        }
    }

    public string HydrationProgressText => $"{HydrationPhaseLabel}... {HydrationCompleted}/{HydrationTotal}";
    public double HydrationProgressFraction => HydrationTotal > 0 ? (double)HydrationCompleted / HydrationTotal : 0;

    // Estimated time left for the WHOLE hydration job (all three passes),
    // not just the current one - UpdateHydrationEta computes the RATE from
    // the current phase alone (HydrationCompleted/_hydrationClock, both
    // reset every phase) and projects it across every item still left in
    // the whole job, so the estimate reacts immediately once a slower/
    // faster phase starts instead of staying dragged toward an earlier
    // phase's very different pace.
    public string HydrationEtaText
    {
        get => _hydrationEtaText;
        private set
        {
            if (!SetProperty(ref _hydrationEtaText, value)) return;
            // HasHydrationEta is computed off this field - without this its
            // own IsVisible binding never re-evaluates past its initial
            // false, so the ETA text stays permanently hidden even once
            // HydrationEtaText itself starts updating.
            OnPropertyChanged(nameof(HasHydrationEta));
        }
    }

    public bool HasHydrationEta => !string.IsNullOrEmpty(HydrationEtaText);

    // Transient message shown when a card is clicked before
    // HydrateLibraryClipsAsync has reached it - see OpenClipAsync. Clears
    // itself after a few seconds via _clipNotReadyMessageTimer.
    public string ClipNotReadyMessage
    {
        get => _clipNotReadyMessage;
        private set
        {
            if (!SetProperty(ref _clipNotReadyMessage, value)) return;
            OnPropertyChanged(nameof(HasClipNotReadyMessage));
        }
    }

    public bool HasClipNotReadyMessage => !string.IsNullOrEmpty(ClipNotReadyMessage);

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
        set
        {
            if (!SetProperty(ref _activeGameDetection, value)) return;
            if (value.IsDetected) EnsureGameCaptureRow(value);
        }
    }

    // A detected game with no catalog entry and no prior override just gets
    // played fine (detection doesn't require a catalog match), but it never
    // showed up in Game Detection settings unless the user found it and
    // manually added it via "add a running game". Auto-adding it here as a
    // custom override row (same shape AddCustomGame produces) means any game
    // you actually play surfaces there on its own, with a sane default
    // display name, so the per-game backend override is discoverable.
    private void EnsureGameCaptureRow(GameDetection detection)
    {
        if (string.IsNullOrWhiteSpace(detection.ExeName)) return;
        if (GameCatalog.BuiltIn.ContainsKey(detection.ExeName)) return;
        if (Settings.GameCaptureOverrides.Any(g => string.Equals(g.ExecutableName, detection.ExeName, StringComparison.OrdinalIgnoreCase))) return;
        // Removing a game adds it here. Without this check the very next
        // detection tick auto-added it straight back, which is why Remove
        // looked like it did nothing for a game that was currently running.
        if (Settings.IgnoredGameExecutables.Contains(detection.ExeName, StringComparer.OrdinalIgnoreCase)) return;

        Settings.GameCaptureOverrides.Add(new GameCaptureOverride
        {
            ExecutableName = detection.ExeName,
            DisplayName = detection.DisplayName,
            CaptureBackend = "Auto"
        });
        SaveSettings();
        RebuildGameCaptureRows();
        GameCatalogChanged?.Invoke(this, EventArgs.Empty);
        AppLog.Info($"Game detection: auto-added {detection.DisplayName} ({detection.ExeName}) to Game Detection settings.");
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

    // Editor's master output volume - MainWindow.axaml.cs forwards changes to
    // PlaybackSession.SetMasterVolume via the ViewModel.PropertyChanged
    // subscription already used for a couple of other view-affecting
    // properties, same pattern as Cs2AutoClipEnabled.
    public double MasterVolumePercent
    {
        get => _masterVolumePercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 150);
            if (!SetProperty(ref _masterVolumePercent, clamped)) return;
            Settings.EditorMasterVolume = clamped;
            SaveSettings();
            OnPropertyChanged(nameof(EffectiveMasterVolumePercent));
            OnPropertyChanged(nameof(IsMasterVolumeNonDefault));
        }
    }

    // Drives the master volume reset button (MainWindow.axaml) - same
    // "only show once it's actually off-default" behavior as
    // TrackLaneViewModel.IsVolumeNonDefault.
    public bool IsMasterVolumeNonDefault => Math.Abs(MasterVolumePercent - 100) > 0.01;

    // Independent of MasterVolumePercent so un-muting restores whatever level
    // was set before - same pattern as TrackLaneViewModel.IsMuted. Not
    // persisted (matches per-track mute, which also resets each session);
    // only the volume LEVEL is a saved preference.
    public bool IsMasterMuted
    {
        get => _isMasterMuted;
        set
        {
            if (!SetProperty(ref _isMasterMuted, value)) return;
            OnPropertyChanged(nameof(EffectiveMasterVolumePercent));
        }
    }

    // What PlaybackSession's output should actually use - 0 while muted, the
    // real percent otherwise. Also what the volume icon's tier (mute/low/
    // high) is driven from, so toggling mute updates the icon exactly like
    // dragging the slider to 0 would.
    public double EffectiveMasterVolumePercent => IsMasterMuted ? 0 : MasterVolumePercent;

    // Scroll-to-zoom on the video (both the normal editor and fullscreen -
    // it's the SAME EditorVideoView control reparented between the two, see
    // MainWindow.axaml.cs's FullscreenButton_OnClick). 1.0 = no zoom;
    // clamped so it can never zoom OUT past the frame (no black margin) or
    // in so far the picture is unusable.
    public double VideoZoom
    {
        get => _videoZoom;
        set
        {
            var clamped = Math.Clamp(value, 1.0, 4.0);
            if (!SetProperty(ref _videoZoom, clamped)) return;
            OnPropertyChanged(nameof(IsVideoZoomed));
            // Re-clamping here (not just relying on VideoPanY's own setter)
            // matters when zooming OUT shrinks the valid pan range - without
            // this, a pan set at 3x zoom would stay at its old value at 1.5x,
            // even though the transform math would then be panning past the
            // frame edge.
            VideoPanY = _videoPanY;
        }
    }

    public bool IsVideoZoomed => VideoZoom > 1.0;

    // Normalized -1..1 - MainWindow.axaml.cs's UpdateVideoTransform turns this
    // into an actual pixel offset against the video's current rendered
    // height and zoom level, since that's layout-dependent and not something
    // a plain binding can compute.
    public double VideoPanY
    {
        get => _videoPanY;
        set => SetProperty(ref _videoPanY, Math.Clamp(value, -1, 1));
    }

    // Called on every clip open (OpenMedia) - a fresh clip should always
    // start at the normal, unzoomed view rather than carrying over whatever
    // zoom/pan the previous clip was left at.
    private void ResetVideoZoom()
    {
        VideoZoom = 1.0;
        VideoPanY = 0;
    }

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

    // ClypDat.App.csproj's SetSourceRevisionId target folds "+<short-hash>" onto
    // AssemblyInformationalVersion at build time (e.g. "0.1.7+a1b2c3d") - a
    // completely separate attribute from AssemblyVersion, which is what
    // AppVersionDisplay/AppUpdateService's update check both read instead,
    // so showing this here doesn't affect update-check behavior at all.
    // Empty whenever git wasn't available at build time (source distributed
    // without .git, git missing from PATH) - the target just leaves the
    // informational version at its plain, hash-less default then.
    private static readonly string CommitHash = ResolveCommitHash();

    private static string ResolveCommitHash()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return string.Empty;

        var plusIndex = informational.IndexOf('+');
        return plusIndex >= 0 && plusIndex + 1 < informational.Length ? informational[(plusIndex + 1)..] : string.Empty;
    }

    public bool HasAppCommit => CommitHash.Length > 0;
    public string AppCommitDisplay => $"({CommitHash})";
    public string AppCommitUrl => $"https://github.com/ClypDat/ClypDat/commit/{CommitHash}";

    public string SelectedSettingsSection
    {
        get => _selectedSettingsSection;
        set => SetProperty(ref _selectedSettingsSection, value);
    }

    public void SelectSettingsSection(string section)
    {
        SelectedSettingsSection = section;
        Settings.LastSettingsSection = section;
        SaveSettings();
    }

    public bool IsCapturingHotkey
    {
        get => _isCapturingHotkey;
        set
        {
            if (!SetProperty(ref _isCapturingHotkey, value)) return;
            OnPropertyChanged(nameof(HotkeyDisplay));
        }
    }

    // Master switch for the buffer. MainWindow watches this property (see its
    // ViewModel.PropertyChanged handler) and starts/stops the capture to
    // match, so flipping it takes effect immediately rather than at the next
    // game-detection tick.
    public bool ReplayBufferEnabled
    {
        get => Settings.ReplayBufferEnabled;
        set
        {
            if (Settings.ReplayBufferEnabled == value) return;
            Settings.ReplayBufferEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReplayBufferStateSummary));
            SaveSettings();
        }
    }

    public string ReplayBufferStateSummary => ReplayBufferEnabled
        ? "Armed - records in the background the instant a game is detected."
        : "Off - nothing is being recorded, and clips can't be saved.";

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

    public bool AutoClippingEnabled
    {
        get => Settings.AutoClipping.Enabled;
        set
        {
            if (Settings.AutoClipping.Enabled == value) return;
            Settings.AutoClipping.Enabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string AutoClipSearchText
    {
        get => _autoClipSearchText;
        set
        {
            if (!SetProperty(ref _autoClipSearchText, value)) return;
            foreach (var game in AutoClipGames)
            {
                game.IsSearchMatch = string.IsNullOrWhiteSpace(value) || game.Name.Contains(value, StringComparison.OrdinalIgnoreCase) || game.Definition.Events.Any(item => item.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
            }
            OnPropertyChanged(nameof(HasAutoClipSearchResults));
        }
    }

    public bool HasAutoClipSearchResults => AutoClipGames.Any(game => game.IsSearchMatch);

    public AutoClipGameViewModel? FindAutoClipGame(string id) => AutoClipGames.FirstOrDefault(game => string.Equals(game.Id, id, StringComparison.OrdinalIgnoreCase));

    private void EnsureAutoClipSettings()
    {
        foreach (var definition in AutoClipCatalog.Active)
        {
            if (!Settings.AutoClipping.Games.TryGetValue(definition.Id, out var game))
            {
                game = new AutoClipGameSettings { ListenerPort = definition.DefaultPort };
                Settings.AutoClipping.Games[definition.Id] = game;
            }
            if (game.ListenerPort == 0) game.ListenerPort = definition.DefaultPort;
            foreach (var item in definition.Events)
            {
                if (!game.Events.ContainsKey(item.Id)) game.Events[item.Id] = DefaultAutoClipEvent(definition.Id, item.Id);
            }
        }
    }

    private static bool DefaultAutoClipEvent(string gameId, string eventId) => (gameId, eventId) switch
    {
        ("cs2", "3k" or "4k" or "ace") => true,
        ("dota2", "triple" or "ultra" or "rampage" or "aegis-snatched") => true,
        ("league", "triple" or "quadra" or "penta" or "baron-steal" or "dragon-steal") => true,
        _ => false
    };

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
                _mediaProbe.MoveCacheFor(videoPath, destinationPath);
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

    public bool ScaleClipsWithWindow
    {
        get => Settings.ScaleClipsWithWindow;
        set
        {
            if (Settings.ScaleClipsWithWindow == value) return;
            Settings.ScaleClipsWithWindow = value;
            OnPropertyChanged();
            SaveSettings();
            // Re-lay the grid immediately against whatever width the window
            // is already at, rather than waiting for the next resize to
            // notice the toggle changed.
            if (_lastCardLayoutWidth > 0) UpdateCardLayout(_lastCardLayoutWidth);
        }
    }

    // Which of the editor's two playback bars is live. MainWindow watches this
    // (its ViewModel.PropertyChanged handler) to build the docked bar on
    // demand and to drop the floating one; the docked bar's own visibility is
    // bound straight to it in XAML.
    public bool EditorHoverBarEnabled
    {
        get => Settings.EditorHoverBarEnabled;
        set
        {
            if (Settings.EditorHoverBarEnabled == value) return;
            Settings.EditorHoverBarEnabled = value;
            OnPropertyChanged();
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
                _mediaProbe.MoveCacheFor(sourcePath, targetPath);
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

        // A network share that's slow or briefly unreachable makes even
        // Directory.Exists block for the OS's SMB timeout (can be several
        // seconds) - offloading it (and the scan below) keeps that off the
        // UI thread instead of freezing the whole window on every refresh.
        if (string.IsNullOrWhiteSpace(Settings.LibraryFolder) || !await Task.Run(() => Directory.Exists(Settings.LibraryFolder)))
        {
            StartLibraryWatcher();
            NotifyLibraryChrome();
            // A network share not mounted yet (ClypDat auto-starting at boot
            // ahead of the OS reconnecting drives is the common case) used
            // to leave the library permanently blank - nothing here ever
            // rechecked, so even once the share came back nothing noticed
            // until the user hit Refresh themselves. Retry on a timer
            // instead; it stops itself the moment a refresh actually finds
            // the folder (see below).
            ScheduleLibraryFolderRetry();
            return;
        }

        _libraryFolderRetryTimer?.Stop();
        _libraryFolderRetryTimer = null;

        LibraryLayout.EnsureRoots(Settings.LibraryFolder);
        if (Settings.LibraryLayoutVersion < LibraryLayout.CurrentVersion)
        {
            await MigrateLibraryLayoutAsync();
        }

        MigrateLegacySessionTitles();
        StartLibraryWatcher();

        var clips = await Task.Run(() => _mediaProbe.EnumerateVideos(Settings.LibraryFolder)
            .Select(file => new ClipCardViewModel(_mediaProbe.CreateLibraryStub(file), Settings.LibraryFolder))
            .OrderByDescending(clip => clip.CreatedAt)
            .ToArray());

        foreach (var clip in clips) AllClips.Add(clip);

        // Every clip here is a brand new ClipCardViewModel, defaulting to
        // matched/visible - RecomputeGameFilterBadges (called from
        // NotifyLibraryChrome below) only reapplies a filter when rebuilding
        // its option list happens to invalidate one, not on every refresh,
        // so a still-active game/clip-type filter was silently dropped and
        // the whole library showed instead.
        ApplyGameFilters();
        ApplyClipTypeFilters();

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
                        _mediaProbe.MoveCacheFor(sourcePath, destinationPath);
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

    // Renames rewrite the sidecars and folders themselves (RenameGameAsync),
    // so there's no display-name mapping layered on top - what the library
    // shows is what's actually on disk.
    private static (string Game, bool Inferred) ResolveLibraryGame(string videoPath, ClipInfo? info)
    {
        if (!string.IsNullOrWhiteSpace(info?.GameDisplayName) && !MedalImportService.IsStructuralFolderName(info.GameDisplayName))
        {
            return (MedalImportService.CanonicalGameName(info.GameDisplayName), false);
        }

        var sourceName = info?.FileTitle ?? Path.GetFileNameWithoutExtension(videoPath);
        if (MedalImportService.TryResolveGameFromFileName(sourceName, out var inferredGame, out _)) return (inferredGame, true);

        var parent = Path.GetFileName(Path.GetDirectoryName(videoPath));
        if (!string.IsNullOrWhiteSpace(parent) &&
            !MedalImportService.IsStructuralFolderName(parent) &&
            !string.Equals(parent, "Clips", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parent, "VODs", StringComparison.OrdinalIgnoreCase))
        {
            return (MedalImportService.CanonicalGameName(parent), false);
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
        // Marked BEFORE any awaits below - the folder watcher's Created event
        // for this same file can arrive on its own thread at any point from
        // here on, and needs to see this entry the moment it's possible for
        // the event to fire, not after this method's own (slower) probe work
        // finishes.
        _recentlySelfAddedPaths[filePath] = DateTime.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var media = _mediaProbe.CreateLibraryStub(filePath);
        var existing = AllClips.FirstOrDefault(clip => string.Equals(clip.Path, filePath, StringComparison.OrdinalIgnoreCase));
        ClipCardViewModel clip;
        if (existing is not null)
        {
            existing.UpdateMedia(media);
            clip = existing;
        }
        else
        {
            clip = new ClipCardViewModel(media, Settings.LibraryFolder);
            var insertIndex = 0;
            while (insertIndex < AllClips.Count && AllClips[insertIndex].CreatedAt > clip.CreatedAt) insertIndex++;
            AllClips.Insert(insertIndex, clip);
        }

        NotifyLibraryChrome();
        AppLog.Debug($"Library quick add: {filePath} in {clock.ElapsedMilliseconds}ms.");

        // Metadata first (fast - a probe-cache hit is a JSON read, and even a
        // real ffprobe is far lighter than image generation) - this alone is
        // enough for ClipCardViewModel.IsHydrated, unblocking the clip for
        // opening. Thumbnail+filmstrip generation (up to ~16 ffmpeg processes
        // between the two) used to run INLINE here first, before the clip
        // was even openable - saving a clip and immediately clicking it made
        // the editor's own playback startup (LibVLC decode, audio chunk
        // extraction) compete directly with that generation for CPU, which
        // is what made the clip you JUST saved specifically stutter/delay
        // audio on open (an already-hydrated older clip has none of this
        // cost - its images are already cached). Images now fill in after,
        // in the background, via HydrateClipImagesAsync.
        var probedMedia = await _mediaProbe.ProbeMetadataAsync(filePath);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            clip.UpdateMedia(probedMedia);
            // Guarded on IsEditorVisible too - AddOrUpdateLibraryClipAsync
            // also runs after the editor closes (to refresh the library
            // card), and SelectedVideoPath still points at that clip then.
            // Without the guard, OpenMedia's unconditional IsEditorVisible =
            // true would pop the editor back open right after the user
            // closed it.
            if (IsEditorVisible && string.Equals(SelectedVideoPath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                OpenMedia(probedMedia, preserveEditorText: true);
            }
        });

        _ = HydrateClipImagesAsync(clip, filePath);
    }

    // Second stage of AddOrUpdateLibraryClipAsync - thumbnail/filmstrip
    // generation, run after the clip is already openable rather than
    // blocking on it. If the editor happens to already be open on this
    // exact clip (opened while its images were still generating), patches
    // the thumbnail/filmstrip directly into the live editor state instead
    // of re-running OpenMedia - OpenMedia resets zoom/trim/playhead/timeline
    // tracks from scratch, which would visibly jump/reset the editor out
    // from under the user for what should be an invisible background update.
    private async Task HydrateClipImagesAsync(ClipCardViewModel clip, string filePath)
    {
        try
        {
            var thumbnailPath = await _mediaProbe.EnsureThumbnailAsync(filePath, clip.Duration);
            var filmstripPath = await _mediaProbe.EnsureFilmstripAsync(filePath, clip.Duration);
            if (string.IsNullOrEmpty(thumbnailPath) && string.IsNullOrEmpty(filmstripPath)) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var updated = clip.Media;
                if (!string.IsNullOrEmpty(thumbnailPath)) updated = updated with { ThumbnailPath = thumbnailPath };
                if (!string.IsNullOrEmpty(filmstripPath)) updated = updated with { FilmstripPath = filmstripPath };
                clip.UpdateMedia(updated);

                if (!IsEditorVisible || !string.Equals(SelectedVideoPath, filePath, StringComparison.OrdinalIgnoreCase)) return;

                if (!string.IsNullOrEmpty(thumbnailPath))
                {
                    SelectedThumbnailPath = thumbnailPath;
                    SelectedThumbnail = LoadBitmap(thumbnailPath);
                }

                if (!string.IsNullOrEmpty(filmstripPath))
                {
                    var filmstrip = LoadBitmap(filmstripPath);
                    foreach (var track in TimelineTracks.Where(track => track.IsVideo))
                    {
                        track.Filmstrip = filmstrip;
                    }
                }
            });
        }
        catch
        {
            // Images are pure polish - a failed thumbnail/filmstrip generation shouldn't be user-visible.
        }
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
        // Library hydration deliberately keeps running in the background here
        // - opening a clip shouldn't stop the rest of the library from
        // filling in behind it. Only closing ClypDat (Dispose) should stop it.
        var media = _mediaProbe.CreateLibraryStub(filePath);
        OpenMedia(media);
        _ = HydrateSelectedMediaAsync(filePath);
        return Task.CompletedTask;
    }

    // Returns whether the clip actually opened - OpenClipCardAsync (the click
    // handler) skips queuing playback when it didn't. A card can be clicked
    // before HydrateLibraryClipsAsync has reached it (still 0:00, no tracks
    // probed yet); opening it anyway used to show a half-broken editor that
    // silently fixed itself once the background probe caught up. Telling the
    // user to wait instead is clearer than that.
    public Task<bool> OpenClipAsync(ClipCardViewModel clip)
    {
        if (!clip.IsHydrated)
        {
            ClipNotReadyMessage = "Still loading this clip's info - try again in a moment.";
            _clipNotReadyMessageTimer.Stop();
            _clipNotReadyMessageTimer.Start();
            return Task.FromResult(false);
        }

        // See OpenVideoFileAsync - hydration keeps running in the background.
        OpenMedia(clip.Media);
        return Task.FromResult(true);
    }

    private double _lastCardLayoutWidth;

    // Target card width for ScaleClipsWithWindow - picked so a maximized
    // ~2560px-wide (1440p) window lands around 6 columns, per the reference
    // point this setting was calibrated against.
    private const double ScaledCardTargetWidth = 400;

    // availableWidth is the card grid's OWN width (the library ScrollViewer's
    // rendered width), not the window's. It used to be passed the window
    // width minus a flat 48 for chrome, which stopped being anywhere near
    // right once the sidebar rail and date scrubber took ~130px out of the
    // row - the extra column that assumption bought never fit, leaving a
    // column-wide empty gap down the right of the library.
    public void UpdateCardLayout(double availableWidth)
    {
        _lastCardLayoutWidth = availableWidth;
        // The width handed in is the ScrollViewer's OUTER width, which includes
        // the strip its vertical scrollbar occupies - the WrapPanel inside
        // actually gets less than that. Both formulas below divide the width up
        // almost exactly, so those few pixels were enough to push the last card
        // of every row onto the next one: a window sized for three columns laid
        // out two and left a card's worth of dead space down the right side.
        const double scrollbarAllowance = 20;
        var contentWidth = Math.Max(320, availableWidth - scrollbarAllowance);

        if (Settings.ScaleClipsWithWindow)
        {
            // More columns on a wider window instead of the same fixed
            // count just stretching wider - floor so a partial column never
            // overflows, clamped to a sane [2, 10] range. 24 matches each
            // card's own trailing Margin (MainWindow.axaml's WrapPanel item,
            // Margin="4,4,20,24" - 4 left + 20 right) reserved per column.
            CardColumns = Math.Clamp((int)Math.Floor(contentWidth / ScaledCardTargetWidth), 2, 10);
            CardWidth = Math.Max(220, Math.Floor(contentWidth / CardColumns) - 24);
        }
        else
        {
            // Fixed three columns. Reserves the same 24px per card the scaled
            // branch does (4 left + 20 right of margin) - the old formula took
            // 64 off the total for three cards needing 72, so the third card
            // never fit and wrapped, which is the other half of the dead space
            // down the right side.
            CardColumns = 3;
            CardWidth = Math.Max(220, Math.Floor(contentWidth / 3) - 24);
        }

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
        HashSet<string>? importedKeys = null;
        foreach (var clip in selected)
        {
            // Read the sidecar's MedalImportKey BEFORE deleting it below - once
            // gone, there's no way to know this clip was ever a Medal import,
            // and its key would stay stuck in the "already imported" history
            // forever, permanently blocking a re-import of the same clip.
            var medalImportKey = ClipInfoSidecar.Load(Settings.LibraryFolder, clip.Path)?.MedalImportKey;
            if (!string.IsNullOrWhiteSpace(medalImportKey))
            {
                importedKeys ??= LoadMedalImportHistory();
                importedKeys.Remove(medalImportKey);
            }

            // Suppresses the watcher's own Deleted echo for this path -
            // reusing the same dictionary/window AddOrUpdateLibraryClipAsync
            // marks self-adds with, just for the delete side of the same
            // "don't redundantly react to our own change" pattern.
            _recentlySelfAddedPaths[clip.Path] = DateTime.UtcNow;
            await FileRetry.RunAsync(() => File.Delete(clip.Path), $"Delete clip {clip.Path}");
            _mediaProbe.DeleteCacheFor(clip.Path);
            ClipEditSidecar.Delete(Settings.LibraryFolder, clip.Path);
            ClipInfoSidecar.Delete(Settings.LibraryFolder, clip.Path);
            Settings.ClipEdits.Remove(ClipEditKey(clip.Path));
            AllClips.Remove(clip);
        }

        if (importedKeys is not null) PersistMedalImportHistory(importedKeys);

        // Every currently-selected clip just got deleted above, so a plain
        // ClearSelection is correct here (not per-clip SetClipSelected) and
        // cheaper.
        ClearSelection();
        SaveSettings();
        NotifyLibraryChrome();
        return selected.Length;
    }

    public async Task DeleteClipAsync(ClipCardViewModel clip)
    {
        // See DeleteSelectedAsync - must read this before the sidecar is deleted.
        var medalImportKey = ClipInfoSidecar.Load(Settings.LibraryFolder, clip.Path)?.MedalImportKey;
        if (!string.IsNullOrWhiteSpace(medalImportKey))
        {
            var importedKeys = LoadMedalImportHistory();
            importedKeys.Remove(medalImportKey);
            PersistMedalImportHistory(importedKeys);
        }

        _recentlySelfAddedPaths[clip.Path] = DateTime.UtcNow;
        await FileRetry.RunAsync(() => File.Delete(clip.Path), $"Delete clip {clip.Path}");
        _mediaProbe.DeleteCacheFor(clip.Path);
        ClipEditSidecar.Delete(Settings.LibraryFolder, clip.Path);
        ClipInfoSidecar.Delete(Settings.LibraryFolder, clip.Path);
        Settings.ClipEdits.Remove(ClipEditKey(clip.Path));

        SaveSettings();
        RemoveClipFromLibrary(clip);
    }

    // Deleting/renaming/re-titling one clip used to call RefreshLibraryAsync,
    // which clears and re-enumerates the WHOLE library and restarts
    // hydration for every card - a full grid rebuild (lost scroll position,
    // hover state, and a re-probe of every other clip) for what's really a
    // one-card change. This just pulls the single affected card out in
    // place instead.
    private void RemoveClipFromLibrary(ClipCardViewModel clip)
    {
        if (clip.IsSelected) SetClipSelected(clip, false);
        AllClips.Remove(clip);
        NotifyLibraryChrome();
    }

    public async Task RenameClipAsync(ClipCardViewModel clip, string newTitle)
    {
        var title = newTitle?.Trim() ?? string.Empty;
        var sanitizedTitle = SanitizeFileTitle(title);
        if (string.IsNullOrWhiteSpace(sanitizedTitle)) return;

        var oldPath = clip.Path;
        var existingInfo = ClipInfoSidecar.Load(Settings.LibraryFolder, oldPath);
        // The sidecar carries what the user actually typed; only the file on
        // disk gets the substituted characters.
        var updatedInfo = existingInfo is null
            ? new ClipInfo(null, null, title)
            : existingInfo with { FileTitle = title };
        ClipInfoSidecar.Save(Settings.LibraryFolder, oldPath, updatedInfo);

        var newPath = await RenameClipFileAsync(clip, sanitizedTitle);
        SyncOpenEditorPathIfNeeded(oldPath, newPath);
        await RefreshClipInPlaceAsync(clip, newPath);
    }

    // Only the FILENAME needs to be legal - the title kept in the sidecar (and
    // shown on the card) stays exactly as it was typed, punctuation and all.
    private static string SanitizeFileTitle(string title) => ClipFileNaming.SanitizeSegment(title);

    // Renames the video file on disk, swapping just the title portion (game
    // name / custom label) while preserving whatever trailing date/time
    // suffix ClipFileNaming appended to the original filename - shared by
    // both RenameClipAsync (auto-clips/Medal imports, edits FileTitle) and
    // RenameClipTitleAsync (manual clips, edits CustomTitle), so naming a
    // clip in the Library keeps File Explorer's filename in sync either way.
    private async Task<string> RenameClipFileAsync(ClipCardViewModel clip, string sanitizedTitle)
    {
        var oldPath = clip.Path;
        var oldStem = Path.GetFileNameWithoutExtension(oldPath);
        var strippedOld = ClipFileNaming.StripTimestampSuffix(oldStem);
        var suffix = oldStem[strippedOld.Length..];
        var newStem = sanitizedTitle + suffix;
        var directory = Path.GetDirectoryName(oldPath) ?? Settings.LibraryFolder;
        var newPath = ClipFileNaming.BuildUniquePath(directory, newStem + Path.GetExtension(oldPath));

        await FileRetry.RunAsync(() => File.Move(oldPath, newPath), $"Rename clip {oldPath} -> {newPath}");
        MoveClipSidecars(oldPath, newPath);
        _mediaProbe.MoveCacheFor(oldPath, newPath);
        return newPath;
    }

    // A rename can target the clip that's currently open in the editor -
    // without this its SelectedVideoPath would keep pointing at the
    // now-moved/renamed file. Only the identity fields are touched (path/
    // name/title), not a full OpenMedia reset, so trim/zoom/playback
    // position aren't disturbed for what's just a rename, not new media.
    private void SyncOpenEditorPathIfNeeded(string oldPath, string newPath)
    {
        if (!IsEditorVisible || !string.Equals(SelectedVideoPath, oldPath, StringComparison.OrdinalIgnoreCase)) return;

        var newName = Path.GetFileNameWithoutExtension(newPath);
        SelectedVideoPath = newPath;
        SelectedVideoName = newName;
        EditorTitle = newName;
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

    // Renames the card's own display label (shown in place of "Clip from
    // {date}" for non-auto-clip cards) - unlike RenameClipAsync above this
    // never touches FileTitle/GameDisplayName, so it can't clobber a Medal
    // import's original event title. It DOES still rename the file on disk
    // (same suffix-preserving swap as RenameClipAsync) so File Explorer's
    // filename and an already-open editor's title stay in sync with the new
    // custom label too. An empty title clears it back to "Clip from {date}"
    // and leaves the file's name alone - there's no sensible "revert" name
    // to rename it back to.
    public async Task RenameClipTitleAsync(ClipCardViewModel clip, string newCustomTitle)
    {
        var sanitized = newCustomTitle.Trim();
        var existingInfo = ClipInfoSidecar.Load(Settings.LibraryFolder, clip.Path);
        var updatedInfo = (existingInfo ?? new ClipInfo(null, null)) with
        {
            CustomTitle = string.IsNullOrWhiteSpace(sanitized) ? null : sanitized
        };

        var sanitizedForFile = string.IsNullOrWhiteSpace(sanitized) ? string.Empty : SanitizeFileTitle(sanitized);
        if (string.IsNullOrWhiteSpace(sanitizedForFile))
        {
            ClipInfoSidecar.Save(Settings.LibraryFolder, clip.Path, updatedInfo);
            // The video file itself is untouched - re-running UpdateMedia with
            // the SAME MediaFileInfo just makes the card reload the sidecar it
            // owns and re-fire its display-label bindings, no re-probe/full
            // refresh needed.
            clip.UpdateMedia(clip.Media);
            return;
        }

        var oldPath = clip.Path;
        ClipInfoSidecar.Save(Settings.LibraryFolder, oldPath, updatedInfo);
        var newPath = await RenameClipFileAsync(clip, sanitizedForFile);
        SyncOpenEditorPathIfNeeded(oldPath, newPath);
        await RefreshClipInPlaceAsync(clip, newPath);
    }

    // Renaming used to RemoveClipFromLibrary + AddOrUpdateLibraryClipAsync -
    // correct (the probe/thumbnail/waveform cache is keyed off the path, so
    // the moved file needs a fresh probe under its new path regardless), but
    // visibly janky: the card's whole WrapPanel container got torn down and
    // rebuilt from scratch, so it flickered out of the grid and back in a
    // moment later instead of just updating in place. Re-probing straight
    // onto the SAME ClipCardViewModel (still sitting at its same spot in
    // AllClips) gets the fresh data without ever removing it from the grid.
    private async Task RefreshClipInPlaceAsync(ClipCardViewModel clip, string newPath)
    {
        // Same self-change suppression AddOrUpdateLibraryClipAsync marks for
        // its own path - otherwise the watcher's own Renamed echo for this
        // exact move triggers a redundant full ScheduleLibraryRefresh right
        // behind it.
        _recentlySelfAddedPaths[newPath] = DateTime.UtcNow;

        clip.UpdateMedia(_mediaProbe.CreateLibraryStub(newPath));
        var probedMedia = await _mediaProbe.ProbeMetadataAsync(newPath);
        clip.UpdateMedia(probedMedia);
        _ = HydrateClipImagesAsync(clip, newPath);
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

        // The running-process list only ever got filled when Settings was
        // opened or Refresh was pressed, so onboarding's Chat Audio App picker
        // started empty and looked broken until the user hit Refresh.
        _ = RefreshOpenProcessesAsync();

        // Recorded the moment the walkthrough is SHOWN, not when it's
        // finished. HasSeenOnboarding is false only while no settings file
        // exists, but any save before the user finishes - changing a setting,
        // picking a folder, or just closing the window, which saves bounds -
        // writes that false to disk and makes it stick. Anyone who closed the
        // app without completing the walkthrough then got it again on every
        // single launch, forever. It can still be replayed deliberately from
        // Settings > About > Show Walkthrough.
        if (!Settings.HasSeenOnboarding)
        {
            Settings.HasSeenOnboarding = true;
            SaveSettings();
        }
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
        RebuildGameCaptureRows();
        GameCatalogChanged?.Invoke(this, EventArgs.Empty);
        AppLog.Info($"Game detection: user excluded {executableName}.");
    }

    public void RemoveIgnoredGameExecutable(string executableName)
    {
        if (Settings.IgnoredGameExecutables.RemoveAll(name => string.Equals(name, executableName, StringComparison.OrdinalIgnoreCase)) == 0) return;
        SaveSettings();
        SyncIgnoredGameExecutableRows();
        RebuildGameCaptureRows();
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

    // Handles both a user-added custom row (delete its override entry) and a
    // built-in catalog row (nothing to delete there - GameCatalog.BuiltIn is a
    // static dict, not per-user data). Either way, excluding the exe is what
    // actually makes removal stick: RebuildGameCaptureRows filters ignored
    // exes out of the built-in list too, and detection itself skips anything
    // on the ignore list, so it won't just reappear next time it's opened.
    public void RemoveGame(GameBackendRowViewModel row)
    {
        if (row.IsCustom)
        {
            Settings.GameCaptureOverrides.RemoveAll(g => string.Equals(g.ExecutableName, row.ExecutableName, StringComparison.OrdinalIgnoreCase));
            SaveSettings();
        }
        AddIgnoredGameExecutable(row.ExecutableName);
    }

    private void RebuildGameCaptureRows()
    {
        foreach (var row in GameCaptureRows) row.PropertyChanged -= GameCaptureRow_OnPropertyChanged;
        GameCaptureRows.Clear();

        var builtIn = GameCatalog.BuiltIn
            .Where(kv => !Settings.IgnoredGameExecutables.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .Select(kv => (ExecutableName: kv.Key, DisplayName: kv.Value, IsCustom: false));
        var custom = Settings.GameCaptureOverrides
            .Where(g => !GameCatalog.BuiltIn.ContainsKey(g.ExecutableName))
            .Select(g => (ExecutableName: g.ExecutableName, DisplayName: g.DisplayName, IsCustom: true));

        // One alphabetical list instead of "sorted built-ins, then whatever
        // order custom/auto-added games happened to land in Settings" - a
        // newly detected game should slot in by name, not always show up at
        // the bottom.
        foreach (var entry in builtIn.Concat(custom).OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var overrideEntry = Settings.GameCaptureOverrides.FirstOrDefault(g => string.Equals(g.ExecutableName, entry.ExecutableName, StringComparison.OrdinalIgnoreCase));
            var backend = ReplayBackends.FirstOrDefault(preset => string.Equals(preset.Value, overrideEntry?.CaptureBackend, StringComparison.OrdinalIgnoreCase))
                          ?? ReplayBackends.First(preset => preset.Value == "Auto");
            var row = new GameBackendRowViewModel(entry.ExecutableName, entry.DisplayName, entry.IsCustom, GameCatalog.AntiCheatSensitive.Contains(entry.ExecutableName), backend);
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
        "obs64.exe", "obs32.exe", "clypdat.exe", "code.exe", "notion.exe"
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
        ResetVideoZoom();
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
            ? $"Video Quality: {ResolutionLabel(media.Height)}{FpsSuffix(media.Fps)}"
            : "Video Quality: Unknown";
        SelectedSize = $"Size: {FormatBytes(media.SizeBytes)}";
        var isMedalImport = !string.IsNullOrWhiteSpace(ClipInfoSidecar.Load(Settings.LibraryFolder, media.Path)?.MedalImportKey);
        SelectedCaptureBackend = isMedalImport
            ? "Imported from Medal"
            : (string.IsNullOrWhiteSpace(media.CaptureBackend) ? string.Empty : $"Captured with: {ClipMetadataTagger.NormalizeBackendLabel(media.CaptureBackend)}");
        SelectedMetadata = $"{SelectedQuality} - {SelectedSize}";
        Duration = media.Duration;
        CurrentTime = TimeSpan.Zero;
        TrimStart = TimeSpan.Zero;
        TrimEnd = media.Duration;
        IsPlaying = false;
        TimelineTracks.Clear();

        var hasVideo = false;
        var audioIndex = 0;
        // Medal usually exports two audio streams: a full pre-mix ("All
        // Audio" - game+mic+everything combined) first, then a second,
        // narrower one ("All PC Audio") after it. The pre-mix just duplicates
        // content the other track(s) already carry, so for a Medal import it's
        // dropped before it's ever added to TimelineTracks - not shown, not
        // muted, not selectable, just never exists as far as the editor or
        // playback (which builds its audio list FROM TimelineTracks, see
        // MainWindow.axaml.cs's StartEditorPlaybackAsync) are concerned.
        //
        // Only when there IS something else to fall back on, though. A Medal
        // clip exported with everything mixed down to a single track has that
        // one track AS its audio, and dropping it left the clip silent with an
        // empty timeline - the import looked broken rather than mixed.
        var medalAudioTrackCount = isMedalImport
            ? media.Tracks.Count(track => track.Type == "audio")
            : 0;
        var dropMedalPreMix = medalAudioTrackCount > 1;
        var skippedMedalPreMixTrack = false;
        var filmstrip = LoadBitmap(media.FilmstripPath);
        foreach (var track in media.Tracks)
        {
            if (track.Type == "subtitle") continue;
            if (dropMedalPreMix && track.Type == "audio" && !skippedMedalPreMixTrack)
            {
                skippedMedalPreMixTrack = true;
                continue;
            }

            var color = track.Type switch
            {
                "video" => "#05C7B7",
                "audio" => AudioColor(audioIndex),
                _ => "#607080"
            };
            var label = track.Type == "audio"
                ? AudioLaneLabel(track.Label, audioIndex)
                : "Video";
            var lane = new TrackLaneViewModel(track.Index, label, track.Type, color, track.Type == "audio", track.VolumePercent);
            if (track.Type == "video")
            {
                hasVideo = true;
                lane.Filmstrip = filmstrip;
            }
            TimelineTracks.Add(lane);
            if (track.Type == "audio") audioIndex++;
        }

        if (!hasVideo)
        {
            TimelineTracks.Insert(0, new TrackLaneViewModel(0, "Video", "video", "#05C7B7", false) { Filmstrip = filmstrip });
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
        OnPropertyChanged(nameof(LibrarySizeDisplay));
        OnPropertyChanged(nameof(HasDriveStats));
        OnPropertyChanged(nameof(LibraryDriveFreeOfTotalDisplay));
        OnPropertyChanged(nameof(LibraryDriveUsedPercentDisplay));
        OnPropertyChanged(nameof(LibraryDriveUsedFraction));
        OnPropertyChanged(nameof(LibraryUsedFractionOfDrive));
        OnPropertyChanged(nameof(LibraryOtherUsedFractionOfDrive));
        OnPropertyChanged(nameof(LibraryClipsUsageDisplay));
        OnPropertyChanged(nameof(LibraryOtherUsageDisplay));
        OnPropertyChanged(nameof(LibraryPossibleClipsDisplay));
        NotifyStorageChrome();
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

    // How many games the sidebar rail shows inline before the rest fold into
    // the automatic "More Games" folder.
    private const int TopGameRailCount = 5;
    private const string AutomaticFolderId = "__more__";

    // What the sidebar rail actually renders, top to bottom: a mix of
    // FilterOptionViewModel (a loose game) and GameRailFolderViewModel (a
    // folder, expandable in place). Rebuilt by RebuildGameRail - see there for
    // the automatic-vs-customised split.
    public ObservableCollection<object> GameRailEntries { get; } = new();

    /// <summary>
    /// Renames a game for real: every clip of it moves into the new game's
    /// folder under a filename built from the new name, its sidecar's stored
    /// game is rewritten, and the settings that reference the old name follow.
    /// A display-only override would have been far less work, but it leaves
    /// the library on disk saying something different to the library on
    /// screen - and the folder names are what a user browsing their clips in
    /// Explorer actually sees.
    ///
    /// Per-clip failures (a file open in another program, a permission
    /// problem) are counted and reported rather than aborting the rest: a
    /// half-renamed game still shows both names and can simply be renamed
    /// again.
    /// </summary>
    public async Task<(int Renamed, int Failed)> RenameGameAsync(string currentName, string newName)
    {
        newName = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentName) || string.IsNullOrWhiteSpace(newName)) return (0, 0);
        if (string.Equals(currentName, newName, StringComparison.Ordinal)) return (0, 0);

        var libraryRoot = Settings.LibraryFolder;
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot)) return (0, 0);

        var targets = AllClips
            .Where(clip => string.Equals(clip.GameFilterKey, currentName, StringComparison.OrdinalIgnoreCase))
            .Select(clip => (clip.Path, clip.Duration))
            .ToArray();

        var renamed = 0;
        var failed = 0;
        var moves = new List<(string OldPath, string NewPath)>();

        foreach (var (path, knownDuration) in targets)
        {
            var destination = await MoveClipToGameAsync(path, knownDuration, newName);
            if (destination is null)
            {
                failed++;
                continue;
            }

            moves.Add((path, destination));
            renamed++;
        }

        // Anything else holding the old name. The capture-backend rows are
        // keyed by executable, so only their label needs rewriting.
        foreach (var over in Settings.GameCaptureOverrides.Where(row => string.Equals(row.DisplayName, currentName, StringComparison.OrdinalIgnoreCase)))
        {
            over.DisplayName = newName;
        }

        // Any display-only override left over from before renames moved files.
        Settings.GameDisplayNameOverrides.Remove(currentName);
        foreach (var stale in Settings.GameDisplayNameOverrides
                     .Where(pair => string.Equals(pair.Value, currentName, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            Settings.GameDisplayNameOverrides.Remove(stale);
        }

        // The icon cache is keyed by display name, so carry the artwork across
        // rather than making the new name resolve from scratch - which for a
        // renamed game often finds nothing, the rename frequently being the
        // thing that made the old name unrecognisable in the first place.
        GameIconService.CopyCachedIcon(currentName, newName);

        // The game's own folders are left behind empty by the moves above.
        RemoveEmptyGameFolder(LibraryLayout.VideoDirectory(libraryRoot, TimeSpan.Zero, currentName));
        RemoveEmptyGameFolder(LibraryLayout.VodDirectory(libraryRoot, currentName));

        RebuildGameCaptureRows();
        SaveSettings();

        // The cards that moved are updated in place rather than rebuilding the
        // library: a full refresh clears every card, re-enumerates the folder
        // and re-reads a sidecar per clip, which on a big library is a visible
        // stall and a scroll position lost - for what is only ever a change of
        // name and folder on a handful of clips ClypDat has just moved itself.
        foreach (var (oldPath, newPath) in moves)
        {
            var card = AllClips.FirstOrDefault(clip => string.Equals(clip.Path, oldPath, StringComparison.OrdinalIgnoreCase));
            card?.UpdateMedia(_mediaProbe.CreateLibraryStub(newPath));
        }

        // Selection is tracked by path, and every one of those just changed.
        ClearSelection();
        RecomputeGameFilterBadges();
        NotifyLibraryChrome();
        OnPropertyChanged(nameof(LibraryTitle));

        AppLog.Info($"Game renamed: '{currentName}' -> '{newName}' ({renamed} clips moved, {failed} failed).");
        return (renamed, failed);
    }

    /// <summary>
    /// Files one clip under a game: moves it into that game's folder with a
    /// filename built from the new name, brings its sidecars and per-clip
    /// edits along, and records the game in its .info.json. Returns the new
    /// path, or null if it couldn't be moved. Shared by renaming a whole game
    /// and by assigning a game to individual clips.
    /// </summary>
    private async Task<string?> MoveClipToGameAsync(string path, TimeSpan knownDuration, string game)
    {
        var libraryRoot = Settings.LibraryFolder;
        try
        {
            if (!File.Exists(path)) return null;

            // Duration decides Clips/ vs VODs/, so it has to be right even for
            // a clip hydration hasn't reached yet.
            var duration = knownDuration > TimeSpan.Zero ? knownDuration : await _mediaProbe.GetDurationAsync(path);
            var info = ClipInfoSidecar.Load(libraryRoot, path);
            var timestamp = info?.CapturedAt?.LocalDateTime ?? File.GetCreationTime(path);
            var title = string.IsNullOrWhiteSpace(info?.FileTitle)
                ? ClipFileNaming.StripTimestampSuffix(Path.GetFileNameWithoutExtension(path))
                : info.FileTitle;

            var destinationDirectory = LibraryLayout.VideoDirectory(libraryRoot, duration, game);
            Directory.CreateDirectory(destinationDirectory);
            var fileName = ClipFileNaming.BuildFileName(title, timestamp, Path.GetExtension(path), Settings.ClipFileNameScheme, Settings.CustomClipFileNameTemplate, game);
            var desiredPath = Path.Combine(destinationDirectory, fileName);
            var destinationPath = string.Equals(path, desiredPath, StringComparison.OrdinalIgnoreCase)
                ? path
                : ClipFileNaming.BuildUniquePath(destinationDirectory, fileName);

            if (!string.Equals(path, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(path, destinationPath);
                MoveClipSidecars(path, destinationPath);
                _mediaProbe.MoveCacheFor(path, destinationPath);
                if (Settings.ClipEdits.Remove(ClipEditKey(path), out var edit)) Settings.ClipEdits[ClipEditKey(destinationPath)] = edit;
                // Same marker AddOrUpdateLibraryClipAsync uses: the folder
                // watcher will see the move, and the caller updates the card
                // itself.
                _recentlySelfAddedPaths[destinationPath] = DateTime.UtcNow;
                _recentlySelfAddedPaths[path] = DateTime.UtcNow;
            }

            ClipInfoSidecar.Save(libraryRoot, destinationPath, new ClipInfo(
                game,
                info?.AutoClipEventType,
                title,
                info?.CapturedAt?.LocalDateTime ?? timestamp,
                info?.MedalImportKey));
            return destinationPath;
        }
        catch (Exception error)
        {
            AppLog.Error($"Could not file {path} under '{game}'", error);
            return null;
        }
    }

    /// <summary>
    /// Assigns a game to specific clips - the way out of "Unknown Game" and
    /// "No game detected", where detection had nothing to go on at capture
    /// time and the clips are all lumped together despite being from different
    /// games. Renaming the group would be wrong for exactly that reason, so
    /// this works per clip.
    /// </summary>
    public async Task<(int Moved, int Failed)> SetClipsGameAsync(IReadOnlyList<ClipCardViewModel> clips, string game)
    {
        game = game?.Trim() ?? string.Empty;
        if (clips.Count == 0 || string.IsNullOrWhiteSpace(game)) return (0, 0);

        var libraryRoot = Settings.LibraryFolder;
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot)) return (0, 0);

        var previousGames = clips
            .Select(clip => clip.GameFilterKey)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var moved = 0;
        var failed = 0;
        foreach (var clip in clips.ToArray())
        {
            var destination = await MoveClipToGameAsync(clip.Path, clip.Duration, game);
            if (destination is null)
            {
                failed++;
                continue;
            }

            clip.UpdateMedia(_mediaProbe.CreateLibraryStub(destination));
            moved++;
        }

        // A game the last of its clips just left has an empty folder behind it.
        foreach (var previous in previousGames.Where(name => !string.Equals(name, game, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveEmptyGameFolder(LibraryLayout.VideoDirectory(libraryRoot, TimeSpan.Zero, previous));
            RemoveEmptyGameFolder(LibraryLayout.VodDirectory(libraryRoot, previous));
        }

        SaveSettings();
        ClearSelection();
        RecomputeGameFilterBadges();
        NotifyLibraryChrome();
        OnPropertyChanged(nameof(LibraryTitle));
        AppLog.Info($"Clips filed under '{game}': {moved} moved, {failed} failed.");
        return (moved, failed);
    }

    private static void RemoveEmptyGameFolder(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception error)
        {
            AppLog.Error($"Game rename: could not remove empty folder {directory}", error);
        }
    }

    private bool _isRefreshingGameIcons;
    private string _gameIconRefreshStatus = string.Empty;

    public bool IsRefreshingGameIcons
    {
        get => _isRefreshingGameIcons;
        private set => SetProperty(ref _isRefreshingGameIcons, value);
    }

    public string GameIconRefreshStatus
    {
        get => _gameIconRefreshStatus;
        private set => SetProperty(ref _gameIconRefreshStatus, value);
    }

    /// <summary>
    /// Settings > Game Detection's "Refresh game icons". Icons are cached
    /// forever once found, which is right almost always and useless the one
    /// time a game resolved to a launcher's logo or the wrong store entry -
    /// this is the way out of that without going and deleting AppData by hand.
    /// Drops the cached images, refetches the curated list ignoring its
    /// once-a-day window, and looks every game up again from scratch.
    /// </summary>
    public async Task RefreshGameIconsAsync()
    {
        if (IsRefreshingGameIcons) return;
        IsRefreshingGameIcons = true;
        GameIconRefreshStatus = "Refreshing...";
        try
        {
            var removed = await Task.Run(GameIconService.ClearCache);
            await RemoteGameIconsService.ForceRefreshAsync();

            // Clearing the rail's own bitmaps is what makes the games count as
            // missing again - RequestMissingGameIcons goes off HasIcon, and the
            // options are still holding the images loaded before the wipe.
            foreach (var option in GameFilterOptions) option.Icon = null;

            var total = GameFilterOptions.Count;
            RequestMissingGameIcons();

            // The lookups themselves are fire-and-forget (each pushes its own
            // icon into the rail as it lands), so this reports the wipe, not a
            // finished download.
            GameIconRefreshStatus = total == 0
                ? $"Cleared {removed} cached icons."
                : $"Cleared {removed} cached icons - looking up {total} games again.";
            AppLog.Info($"Game icons refreshed by user: {removed} cached images removed, {total} games queued.");
        }
        catch (Exception error)
        {
            AppLog.Error("Game icon refresh failed", error);
            GameIconRefreshStatus = "Refresh failed - see the log for details.";
        }
        finally
        {
            IsRefreshingGameIcons = false;
        }
    }

    // Any game with clips but no icon yet gets one resolved from the internet,
    // so artwork doesn't depend on this install ever having seen the game
    // running. Fire-and-forget: each lookup caches to disk and pushes itself
    // into the rail when it lands, and GameIconService only tries a given game
    // once per session.
    private void RequestMissingGameIcons()
    {
        var missing = GameFilterOptions.Where(option => !option.HasIcon).Select(option => option.Key).ToArray();
        if (missing.Length == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var gameKey in missing)
            {
                if (!await GameIconService.EnsureFromNetworkAsync(gameKey)) continue;
                Dispatcher.UIThread.Post(() => ApplyGameIcon(gameKey));
            }
        });
    }

    // A freshly-extracted icon has to reach the rail rows that were built
    // before it existed - they're the same instances in Top/Overflow, so
    // updating the GameFilterOptions entry updates whichever list shows it.
    public void ApplyGameIcon(string gameKey)
    {
        var icon = GameIconService.TryLoad(gameKey);
        if (icon is null) return;

        foreach (var option in GameFilterOptions)
        {
            if (string.Equals(option.Key, gameKey, StringComparison.OrdinalIgnoreCase)) option.Icon = icon;
        }

        // A folder's collapsed tile shows its FIRST game's icon, computed
        // from Games[0] rather than observed live - if that first game is
        // this one and its icon just landed, the folder needs telling
        // directly, or the tile would sit on its fallback badge until
        // something else happens to rebuild the whole rail.
        foreach (var folder in GameRailEntries.OfType<GameRailFolderViewModel>())
        {
            if (folder.Games.Count > 0 && string.Equals(folder.Games[0].Key, gameKey, StringComparison.OrdinalIgnoreCase))
            {
                folder.NotifyGamesChanged();
            }
        }
    }

    public bool IsGameFilterActive => _activeGameFilters.Count > 0;
    public bool IsClipTypeFilterActive => _activeClipTypeFilters.Count > 0;

    // The rail's own single-select key, for the header Back/Forward history
    // to snapshot - null when nothing's active, or when Combine has more
    // than one held at once (history only tracks the rail's single-select
    // navigation, not the checklist dropdowns' arbitrary multi-select).
    public string? ActiveGameFilterKey => _activeGameFilters.Count == 1 ? _activeGameFilters.First() : null;
    public string? ActiveClipTypeFilterKey => _activeClipTypeFilters.Count == 1 ? _activeClipTypeFilters.First() : null;

    // With Combine off, only one thing in the whole rail can ever be
    // selected at a time (a game and a section can't both be active, see
    // SelectGameSection/SelectClipTypeSection), so "All Clips" there really
    // means "nothing at all is selected" - it has to drop out the moment a
    // game is picked, not just when a clip-type section is. Combine on, the
    // two groups are independent again and "All Clips" goes back to meaning
    // just its own group, same as "All Games" does for the game side.
    public bool IsAllClipsActive => !IsClipTypeFilterActive && (Settings.CombineSidebarFilters || !IsGameFilterActive);

    private const string ClipTypeManual = "Manual";
    private const string ClipTypeAutoClip = "AutoClip";
    private const string ClipTypeVod = "Vod";
    private const string ClipTypeMedalImport = "MedalImport";

    // Rebuilds the Game Filters / Clip Type Filters checklist option lists -
    // works the same for ClypDat-recorded and Medal-imported clips since both
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
                OnGameFilterOptionChanged)
            {
                Icon = GameIconService.TryLoad(game.Key)
            });
        }

        RebuildGameRail();
        RequestMissingGameIcons();

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
            OnPropertyChanged(nameof(IsClipTypeFilterActive));
            OnPropertyChanged(nameof(IsAllClipsActive));
            OnPropertyChanged(nameof(LibraryTitle));
        }
    }

    // ---- Sidebar game rail: folders, ordering, drag/drop ------------------
    //
    // Two modes, chosen entirely by whether Settings.GameRailOrder has
    // anything in it:
    //
    //  - Automatic (order empty): the rail is exactly what it always was -
    //    the most-clipped few games inline, everything else folded into an
    //    unnamed overflow folder. That folder is SYNTHETIC: it has no
    //    GameRailFolder behind it in settings, can't be renamed, and rebuilds
    //    itself from scratch (in clip-count order) every time. Nothing here
    //    is persisted, so a library that's never been organised looks
    //    identical to how it always did, just with the old expand-in-place
    //    chevron replaced by a real (if temporary) folder tile.
    //
    //  - Customised (order non-empty): the rail renders Settings.GameRailOrder
    //    literally - each token is either "game:<key>" (a loose game) or
    //    "folder:<id>" (a real, persisted GameRailFolder). Any current game
    //    that isn't mentioned by any token (new since the last customisation)
    //    is appended at the end, so nothing a user organised can silently
    //    swallow a game that shows up later.
    //
    // The switch from automatic to customised happens exactly once, the first
    // time the user actually does something - see EnsureCustomOrderSeeded.
    // Rendering itself (this method) never mutates settings.
    // GameRailFolderViewModel instances are rebuilt from scratch on every
    // rail action, so IsExpanded can't just live on the (disposable) view
    // model - this is what actually survives across rebuilds, keyed by
    // folder id (the automatic overflow folder's fixed sentinel id while
    // it's still automatic, a real GUID once it's been organised).
    private readonly HashSet<string> _expandedGameFolderIds = new(StringComparer.OrdinalIgnoreCase);

    private void OnGameFolderExpandedChanged(string folderId, bool expanded)
    {
        if (expanded) _expandedGameFolderIds.Add(folderId);
        else _expandedGameFolderIds.Remove(folderId);
    }

    private void RebuildGameRail()
    {
        var byKey = GameFilterOptions.ToDictionary(option => option.Key, StringComparer.OrdinalIgnoreCase);

        GameRailEntries.Clear();

        if (Settings.GameRailOrder.Count == 0)
        {
            foreach (var option in GameFilterOptions.Take(TopGameRailCount))
            {
                GameRailEntries.Add(option);
            }

            var overflow = GameFilterOptions.Skip(TopGameRailCount).ToArray();
            if (overflow.Length > 0)
            {
                var automatic = new GameRailFolderViewModel(AutomaticFolderId, "More Games", isAutomatic: true, OnGameFolderExpandedChanged);
                automatic.SetExpandedSilently(_expandedGameFolderIds.Contains(AutomaticFolderId));
                foreach (var option in overflow) automatic.Games.Add(option);
                automatic.NotifyGamesChanged();
                GameRailEntries.Add(automatic);
            }

            return;
        }

        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Settings.GameRailOrder)
        {
            if (TryParseGameToken(token, out var gameKey))
            {
                if (!byKey.TryGetValue(gameKey, out var option) || !placed.Add(gameKey)) continue;
                GameRailEntries.Add(option);
            }
            else if (TryParseFolderToken(token, out var folderId))
            {
                var folder = Settings.GameRailFolders.FirstOrDefault(f => string.Equals(f.Id, folderId, StringComparison.OrdinalIgnoreCase));
                if (folder is null) continue;

                var folderVm = new GameRailFolderViewModel(folder.Id, folder.Name, isAutomatic: false, OnGameFolderExpandedChanged);
                folderVm.SetExpandedSilently(_expandedGameFolderIds.Contains(folder.Id));
                foreach (var key in folder.GameKeys)
                {
                    if (!byKey.TryGetValue(key, out var option) || !placed.Add(key)) continue;
                    folderVm.Games.Add(option);
                }
                folderVm.NotifyGamesChanged();
                // An empty folder (every game it held has since lost all its
                // clips) still renders - deleting it out from under the user
                // for something as transient as a clip count would be more
                // surprising than a folder that's briefly empty.
                GameRailEntries.Add(folderVm);
            }
        }

        foreach (var option in GameFilterOptions)
        {
            if (placed.Add(option.Key)) GameRailEntries.Add(option);
        }
    }

    // Which folder (if any) currently renders this game - drives the "Remove
    // from folder" menu item's visibility and excludes a game's own folder
    // from its "Move to folder" list. Reads the CURRENT render (GameRailEntries),
    // so it answers correctly in both automatic and customised mode without
    // caring which one is active.
    public string? FindContainingFolderId(string gameKey) =>
        GameRailEntries.OfType<GameRailFolderViewModel>()
            .FirstOrDefault(folder => folder.Games.Any(game => string.Equals(game.Key, gameKey, StringComparison.OrdinalIgnoreCase)))
            ?.Id;

    private static bool TryParseGameToken(string token, out string key)
    {
        if (token.StartsWith("game:", StringComparison.Ordinal))
        {
            key = token["game:".Length..];
            return true;
        }
        key = string.Empty;
        return false;
    }

    private static bool TryParseFolderToken(string token, out string id)
    {
        if (token.StartsWith("folder:", StringComparison.Ordinal))
        {
            id = token["folder:".Length..];
            return true;
        }
        id = string.Empty;
        return false;
    }

    // Converts the automatic layout into a real, persisted one - a no-op once
    // that's already happened. Called from every action that organises the
    // rail (never from rendering), so simply looking at the sidebar never
    // "customises" it, only touching it does. Returns the real folder ID that
    // replaced the automatic overflow folder (AutomaticFolderId), so a caller
    // that captured that sentinel ID from the UI before seeding can resolve
    // it to the folder that actually exists afterward - see TranslateFolderId.
    private string? EnsureCustomOrderSeeded()
    {
        if (Settings.GameRailOrder.Count > 0) return null;

        var top = GameFilterOptions.Take(TopGameRailCount).ToArray();
        var overflow = GameFilterOptions.Skip(TopGameRailCount).ToArray();

        Settings.GameRailOrder = top.Select(option => "game:" + option.Key).ToList();
        if (overflow.Length == 0) return null;

        var folder = new GameRailFolder { Name = "More Games", GameKeys = overflow.Select(option => option.Key).ToList() };
        Settings.GameRailFolders.Add(folder);
        Settings.GameRailOrder.Add("folder:" + folder.Id);
        return folder.Id;
    }

    private static string? TranslateFolderId(string? folderId, string? seededFolderId) =>
        folderId == AutomaticFolderId ? seededFolderId : folderId;

    private static string TranslateFolderToken(string token, string? seededFolderId) =>
        seededFolderId is not null && string.Equals(token, "folder:" + AutomaticFolderId, StringComparison.Ordinal)
            ? "folder:" + seededFolderId
            : token;

    /// <summary>
    /// Moves one game to a specific spot on the rail - top-level (destinationFolderId
    /// null) or inside a folder, before a given game (beforeGameKey) or at the
    /// end. The single primitive behind every game-rail organising action:
    /// reordering, filing into a folder, pulling out of one, and reordering
    /// within one are all just different combinations of these two arguments.
    /// </summary>
    public void RelocateGame(string gameKey, string? destinationFolderId, string? beforeGameKey)
    {
        if (string.IsNullOrWhiteSpace(gameKey)) return;
        var seededFolderId = EnsureCustomOrderSeeded();
        destinationFolderId = TranslateFolderId(destinationFolderId, seededFolderId);

        Settings.GameRailOrder.RemoveAll(token => TryParseGameToken(token, out var key) && string.Equals(key, gameKey, StringComparison.OrdinalIgnoreCase));
        foreach (var folder in Settings.GameRailFolders)
        {
            folder.GameKeys.RemoveAll(key => string.Equals(key, gameKey, StringComparison.OrdinalIgnoreCase));
        }
        NormalizeFolders();

        if (destinationFolderId is null)
        {
            var token = "game:" + gameKey;
            var index = beforeGameKey is null ? -1 : Settings.GameRailOrder.FindIndex(t => TryParseGameToken(t, out var key) && string.Equals(key, beforeGameKey, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = beforeGameKey is null ? -1 : Settings.GameRailOrder.FindIndex(t => TryParseFolderToken(t, out var id) && ContainsGame(id, beforeGameKey));
            if (index >= 0) Settings.GameRailOrder.Insert(index, token);
            else Settings.GameRailOrder.Add(token);
        }
        else
        {
            var folder = Settings.GameRailFolders.FirstOrDefault(f => string.Equals(f.Id, destinationFolderId, StringComparison.OrdinalIgnoreCase));
            if (folder is null) return;

            EnsureFolderTokenPresent(folder.Id);
            var index = beforeGameKey is null ? -1 : folder.GameKeys.FindIndex(key => string.Equals(key, beforeGameKey, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) folder.GameKeys.Insert(index, gameKey);
            else folder.GameKeys.Add(gameKey);
        }

        SaveSettings();
        RebuildGameRail();
    }

    private bool ContainsGame(string folderId, string gameKey) =>
        Settings.GameRailFolders.Any(folder =>
            string.Equals(folder.Id, folderId, StringComparison.OrdinalIgnoreCase) &&
            folder.GameKeys.Any(key => string.Equals(key, gameKey, StringComparison.OrdinalIgnoreCase)));

    private void EnsureFolderTokenPresent(string folderId)
    {
        var token = "folder:" + folderId;
        if (!Settings.GameRailOrder.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase)))
        {
            Settings.GameRailOrder.Add(token);
        }
    }

    // Keeps every persisted folder at 0 or 2+ members - a "folder" of exactly
    // one game isn't a group any more, so moving a game out of a two-game
    // folder dissolves it rather than leaving a single-icon folder behind
    // holding what used to be its partner. Called after anything that can
    // shrink a folder's membership.
    private void NormalizeFolders()
    {
        foreach (var folder in Settings.GameRailFolders.ToArray())
        {
            if (folder.GameKeys.Count == 0)
            {
                Settings.GameRailFolders.Remove(folder);
                Settings.GameRailOrder.RemoveAll(token => TryParseFolderToken(token, out var id) && string.Equals(id, folder.Id, StringComparison.OrdinalIgnoreCase));
            }
            else if (folder.GameKeys.Count == 1)
            {
                var index = Settings.GameRailOrder.FindIndex(token => TryParseFolderToken(token, out var id) && string.Equals(id, folder.Id, StringComparison.OrdinalIgnoreCase));
                var gameToken = "game:" + folder.GameKeys[0];
                Settings.GameRailFolders.Remove(folder);
                if (index >= 0) Settings.GameRailOrder[index] = gameToken;
                else Settings.GameRailOrder.Add(gameToken);
            }
        }
    }

    /// <summary>
    /// Reorders a top-level entry (a loose game or a folder) relative to
    /// another top-level entry. Folders never nest, so this only ever
    /// operates on the top level - dragging a folder onto another folder
    /// reorders them side by side rather than merging.
    /// </summary>
    public void RelocateTopLevelEntry(string sourceToken, string? beforeToken)
    {
        if (string.IsNullOrWhiteSpace(sourceToken) || string.Equals(sourceToken, beforeToken, StringComparison.OrdinalIgnoreCase)) return;
        var seededFolderId = EnsureCustomOrderSeeded();
        sourceToken = TranslateFolderToken(sourceToken, seededFolderId);
        beforeToken = beforeToken is null ? null : TranslateFolderToken(beforeToken, seededFolderId);
        if (string.Equals(sourceToken, beforeToken, StringComparison.OrdinalIgnoreCase)) return;

        Settings.GameRailOrder.RemoveAll(t => string.Equals(t, sourceToken, StringComparison.OrdinalIgnoreCase));
        var index = beforeToken is null ? -1 : Settings.GameRailOrder.FindIndex(t => string.Equals(t, beforeToken, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) Settings.GameRailOrder.Insert(index, sourceToken);
        else Settings.GameRailOrder.Add(sourceToken);

        SaveSettings();
        RebuildGameRail();
    }

    /// <summary>
    /// Creates a brand new folder holding the given games - the "New Folder"
    /// context-menu action (a single game to start; more join later via each
    /// game's own "Move to folder"). Dragging one game onto another reorders
    /// instead of grouping - see GameRailGame_OnDrop's reasoning - so this
    /// isn't reachable from a drag gesture, only the menu.
    /// </summary>
    public void CreateGameFolder(IReadOnlyList<string> gameKeys, string? name = null)
    {
        if (gameKeys.Count == 0) return;
        EnsureCustomOrderSeeded();

        var folder = new GameRailFolder
        {
            Name = string.IsNullOrWhiteSpace(name) ? gameKeys[0] : name.Trim(),
        };
        Settings.GameRailFolders.Add(folder);

        // Anchor the new folder's rail position where the FIRST game being
        // grouped used to sit, rather than at the end - dragging one icon
        // onto another should feel like the pair merged in place, not like
        // everything jumped to the back of the rail.
        var anchorIndex = Settings.GameRailOrder.FindIndex(t => TryParseGameToken(t, out var key) && string.Equals(key, gameKeys[0], StringComparison.OrdinalIgnoreCase));

        foreach (var gameKey in gameKeys)
        {
            Settings.GameRailOrder.RemoveAll(t => TryParseGameToken(t, out var key) && string.Equals(key, gameKey, StringComparison.OrdinalIgnoreCase));
            foreach (var existing in Settings.GameRailFolders) existing.GameKeys.RemoveAll(key => string.Equals(key, gameKey, StringComparison.OrdinalIgnoreCase));
            folder.GameKeys.Add(gameKey);
        }
        NormalizeFolders();

        var folderToken = "folder:" + folder.Id;
        if (anchorIndex >= 0 && anchorIndex <= Settings.GameRailOrder.Count) Settings.GameRailOrder.Insert(anchorIndex, folderToken);
        else Settings.GameRailOrder.Add(folderToken);

        SaveSettings();
        RebuildGameRail();
    }

    public void RenameGameFolder(string folderId, string newName)
    {
        newName = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName)) return;

        // The automatic overflow folder has nothing in settings to rename -
        // naming it for real is itself an organising action.
        var seededFolderId = EnsureCustomOrderSeeded();
        folderId = TranslateFolderId(folderId, seededFolderId) ?? folderId;
        var folder = Settings.GameRailFolders.FirstOrDefault(f => string.Equals(f.Id, folderId, StringComparison.OrdinalIgnoreCase));
        if (folder is null) return;

        folder.Name = newName;
        SaveSettings();
        RebuildGameRail();
    }

    /// <summary>
    /// Ungroups a folder - every game it held goes back to the top level, in
    /// the folder's own former position, in their former order within it.
    /// </summary>
    public void UngroupGameFolder(string folderId)
    {
        var seededFolderId = EnsureCustomOrderSeeded();
        folderId = TranslateFolderId(folderId, seededFolderId) ?? folderId;
        var folder = Settings.GameRailFolders.FirstOrDefault(f => string.Equals(f.Id, folderId, StringComparison.OrdinalIgnoreCase));
        if (folder is null) return;

        var folderToken = "folder:" + folder.Id;
        var index = Settings.GameRailOrder.FindIndex(t => string.Equals(t, folderToken, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = Settings.GameRailOrder.Count;
        else Settings.GameRailOrder.RemoveAt(index);

        Settings.GameRailFolders.Remove(folder);
        var tokens = folder.GameKeys.Select(key => "game:" + key).ToList();
        Settings.GameRailOrder.InsertRange(Math.Min(index, Settings.GameRailOrder.Count), tokens);

        SaveSettings();
        RebuildGameRail();
    }

    // Single-select nav (sidebar Games/Sections shortcuts) - unlike the
    // checklist dropdowns above, only ever one game/section active at a
    // time; passing null clears back to "All". Reuses the same
    // FilterOptionViewModel/Apply*Filters machinery the dropdowns use so
    // both UIs stay in sync with each other and with the underlying set.
    // The rail is single-select WITHIN each group either way - one game, one
    // section, never a stack of them. What CombineSidebarFilters decides is
    // whether the two groups can be held at once ("Fortnite" plus
    // "Auto-Clips") or whether picking from one drops the other, leaving
    // exactly one filter active from the rail at any time.
    public void SelectGameSection(string? gameKey)
    {
        _activeGameFilters.Clear();
        if (gameKey is not null) _activeGameFilters.Add(gameKey);
        foreach (var option in GameFilterOptions) option.SetCheckedSilently(string.Equals(option.Key, gameKey, StringComparison.OrdinalIgnoreCase));
        ApplyGameFilters();

        // Not combined: only one filter is ever active, game or clip-type -
        // this must clear the other side whether gameKey is a real game
        // (picking one) or null (the rail's "All Games"/history restoring to
        // no game), since null is exactly what should also mean "nothing
        // else either" - a "gameKey is not null" guard here used to leave a
        // stale clip-type filter behind whenever this cleared to null.
        if (!Settings.CombineSidebarFilters && _activeClipTypeFilters.Count > 0)
        {
            _activeClipTypeFilters.Clear();
            foreach (var option in ClipTypeFilterOptions) option.SetCheckedSilently(false);
            ApplyClipTypeFilters();
            OnPropertyChanged(nameof(IsClipTypeFilterActive));
        }

        OnPropertyChanged(nameof(IsGameFilterActive));
        OnPropertyChanged(nameof(IsAllClipsActive));
        OnPropertyChanged(nameof(LibraryTitle));
    }

    public void SelectClipTypeSection(string? key)
    {
        _activeClipTypeFilters.Clear();
        if (key is not null) _activeClipTypeFilters.Add(key);
        foreach (var option in ClipTypeFilterOptions) option.SetCheckedSilently(string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase));
        ApplyClipTypeFilters();

        // Same reasoning as SelectGameSection's own clear-the-other-side
        // block - "All Clips" (key null) must clear an active game filter
        // too, not just picking a real clip-type section.
        if (!Settings.CombineSidebarFilters && _activeGameFilters.Count > 0)
        {
            _activeGameFilters.Clear();
            foreach (var option in GameFilterOptions) option.SetCheckedSilently(false);
            ApplyGameFilters();
            OnPropertyChanged(nameof(IsGameFilterActive));
        }

        OnPropertyChanged(nameof(IsClipTypeFilterActive));
        OnPropertyChanged(nameof(IsAllClipsActive));
        OnPropertyChanged(nameof(LibraryTitle));
    }

    // Back to the whole library in one action - both filter groups at once,
    // however many are set. Used by the logo/home button.
    public void ClearAllFilters()
    {
        if (_activeGameFilters.Count == 0 && _activeClipTypeFilters.Count == 0) return;

        _activeGameFilters.Clear();
        _activeClipTypeFilters.Clear();
        foreach (var option in GameFilterOptions) option.SetCheckedSilently(false);
        foreach (var option in ClipTypeFilterOptions) option.SetCheckedSilently(false);
        ApplyGameFilters();
        ApplyClipTypeFilters();
        OnPropertyChanged(nameof(IsGameFilterActive));
        OnPropertyChanged(nameof(IsClipTypeFilterActive));
        OnPropertyChanged(nameof(IsAllClipsActive));
        OnPropertyChanged(nameof(LibraryTitle));
    }

    public bool CombineSidebarFilters
    {
        get => Settings.CombineSidebarFilters;
        set
        {
            if (Settings.CombineSidebarFilters == value) return;
            Settings.CombineSidebarFilters = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAllClipsActive));
            SaveSettings();
            // Turning it off with one of each already held would leave a
            // combination the setting says isn't possible - drop back to just
            // the game, which is the coarser of the two.
            if (!value && _activeGameFilters.Count > 0 && _activeClipTypeFilters.Count > 0)
            {
                SelectClipTypeSection(null);
            }
        }
    }

    private void OnGameFilterOptionChanged(string gameName, bool isChecked)
    {
        if (isChecked) _activeGameFilters.Add(gameName);
        else _activeGameFilters.Remove(gameName);
        ApplyGameFilters();
        OnPropertyChanged(nameof(IsGameFilterActive));
        OnPropertyChanged(nameof(IsAllClipsActive));
        OnPropertyChanged(nameof(LibraryTitle));
    }

    private void OnClipTypeFilterOptionChanged(string key, bool isChecked)
    {
        if (isChecked) _activeClipTypeFilters.Add(key);
        else _activeClipTypeFilters.Remove(key);
        ApplyClipTypeFilters();
        OnPropertyChanged(nameof(IsClipTypeFilterActive));
        OnPropertyChanged(nameof(IsAllClipsActive));
        OnPropertyChanged(nameof(LibraryTitle));
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

        // Each card's own context menu says whether renaming it would hit the
        // whole selection - only the selected cards of a multi-selection do, a
        // right-click on an unselected card being a plain single rename.
        var bulk = SelectedCount > 1;
        foreach (var clip in AllClips)
        {
            var all = bulk && clip.IsSelected;
            clip.RenameActionLabel = all ? "Rename All" : "Rename";
            clip.SetGameActionLabel = all ? $"Change game for {SelectedCount} clips" : "Change game";
        }
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

    // See RefreshLibraryAsync's early-return - only ever scheduled while the
    // configured library folder can't be found, and self-cancels the
    // instant a refresh succeeds. One in-flight timer at a time (a resize/
    // manual Refresh/etc. calling RefreshLibraryAsync again while a retry is
    // already pending would otherwise stack up duplicate timers).
    private void ScheduleLibraryFolderRetry()
    {
        if (_libraryFolderRetryTimer is not null) return;

        _libraryFolderRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _libraryFolderRetryTimer.Tick += async (_, _) =>
        {
            _libraryFolderRetryTimer?.Stop();
            _libraryFolderRetryTimer = null;
            await RefreshLibraryAsync();
        };
        _libraryFolderRetryTimer.Start();
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
        watcher.Deleted += LibraryWatcher_OnDeleted;
        watcher.Renamed += LibraryWatcher_OnRenamed;
        watcher.Error += LibraryWatcher_OnError;
        _libraryWatcher = watcher;
    }

    // A buffer overflow (bursty writes) or the watched share going
    // unreachable both fire this and leave the watcher permanently dead -
    // no further Created/Deleted/Renamed events ever again - which is easy
    // to hit on a flaky/slow network share. Without this, the library just
    // silently stops noticing external changes until the user happens to
    // hit Refresh. Recreate it after a short delay instead.
    private void LibraryWatcher_OnError(object sender, ErrorEventArgs e)
    {
        AppLog.Error("Library folder watcher failed - restarting in 5s.", e.GetException());
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            StartLibraryWatcher();
        });
    }

    private void LibraryWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!MediaProbeService.IsVideoFile(e.FullPath)) return;
        Dispatcher.UIThread.Post(() =>
        {
            // Deliberately NOT checked here - see ScheduleLibraryRefresh/the
            // debounce Tick handler for why the suppression check moved to
            // debounce-fire time instead of event-arrival time.
            _pendingLibraryChangePaths.Add(e.FullPath);
            ScheduleLibraryRefresh();
        });
    }

    // A file deleted outside ClypDat (File Explorer, another process) fires the
    // exact same watcher event a delete triggered from inside ClypDat does -
    // WasRecentlySelfAdded below is what tells the two apart (DeleteClipAsync/
    // DeleteSelectedAsync mark the path first) so an in-app delete's own
    // already-precise cleanup+card removal isn't duplicated here.
    private void LibraryWatcher_OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (!MediaProbeService.IsVideoFile(e.FullPath)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (WasRecentlySelfAdded(e.FullPath)) return;

            CleanUpDeletedClipArtifacts(e.FullPath);

            var clip = AllClips.FirstOrDefault(c => string.Equals(c.Path, e.FullPath, StringComparison.OrdinalIgnoreCase));
            if (clip is not null) RemoveClipFromLibrary(clip);
            else ScheduleLibraryRefresh();
        });
    }

    // Mirrors DeleteClipAsync/DeleteSelectedAsync's own cleanup - a clip
    // deleted outside ClypDat still needs its leftover media-cache (thumbnail/
    // filmstrip/waveform), sidecars, and Medal-import-history entry cleaned
    // up the same way an in-app delete already does, otherwise these pile up
    // forever and (for a Medal import) permanently block re-importing the
    // same clip since its key never leaves the "already imported" history.
    private void CleanUpDeletedClipArtifacts(string path)
    {
        var medalImportKey = ClipInfoSidecar.Load(Settings.LibraryFolder, path)?.MedalImportKey;
        if (!string.IsNullOrWhiteSpace(medalImportKey))
        {
            var importedKeys = LoadMedalImportHistory();
            importedKeys.Remove(medalImportKey);
            PersistMedalImportHistory(importedKeys);
        }

        _mediaProbe.DeleteCacheFor(path);
        ClipEditSidecar.Delete(Settings.LibraryFolder, path);
        ClipInfoSidecar.Delete(Settings.LibraryFolder, path);
        Settings.ClipEdits.Remove(ClipEditKey(path));
        SaveSettings();
    }

    private void LibraryWatcher_OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!MediaProbeService.IsVideoFile(e.FullPath) && !MediaProbeService.IsVideoFile(e.OldFullPath)) return;
        Dispatcher.UIThread.Post(() =>
        {
            // See LibraryWatcher_OnChanged - same deferred-suppression reasoning.
            _pendingLibraryChangePaths.Add(e.FullPath);
            ScheduleLibraryRefresh();
        });
    }

    // AddOrUpdateLibraryClipAsync already fully incorporates a newly-saved
    // (or renamed) clip directly, one card at a time, no full rescan needed
    // - the SAME file's Created/Renamed event still arrives here moments
    // later from the folder watcher regardless, which used to trigger a
    // full RefreshLibraryAsync/re-hydrate of the WHOLE library right on top
    // of that. That redundant pass was both the real reason a save felt
    // slow to "settle" and why the hydration progress banner briefly showed
    // a big library-wide fraction (e.g. "43/44") for what was really just
    // one clip. Suppress the one expected follow-up watcher event per
    // self-added path instead of treating it as an external change - a
    // GENUINE external change (user drops a file in manually, Medal import,
    // another ClypDat instance) was never marked here and still refreshes
    // normally.
    private bool WasRecentlySelfAdded(string path)
    {
        var isSelfAdded = _recentlySelfAddedPaths.TryGetValue(path, out var addedAtUtc) &&
                           DateTime.UtcNow - addedAtUtc < SelfAddedSuppressWindow;
        if (isSelfAdded) _recentlySelfAddedPaths.Remove(path);

        // Opportunistic cleanup of stale entries (a self-add whose watcher
        // event never arrived, e.g. it got coalesced away) - this dictionary
        // is tiny and short-lived, not worth a separate timer just for this.
        if (_recentlySelfAddedPaths.Count > 0)
        {
            foreach (var stale in _recentlySelfAddedPaths
                         .Where(pair => DateTime.UtcNow - pair.Value >= SelfAddedSuppressWindow)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _recentlySelfAddedPaths.Remove(stale);
            }
        }

        return isSelfAdded;
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
            // The network-drive head-start delay (so waveform decode doesn't
            // contend with playback for the same remote file) now lives in
            // MediaProbeService.LoadWaveformsAsync, past its own cache check -
            // an already-cached waveform has nothing to contend with and
            // shouldn't eat that delay just to return a local JSON read.

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

    // Staged in three passes across the WHOLE library, instead of doing
    // metadata+thumbnail+filmstrip for one clip before moving to the next -
    // every clip's duration/tracks (cheap - a probe-cache hit is just a JSON
    // read, and even a real ffprobe is far lighter than image generation)
    // lands FIRST, so every card stops showing 0:00 as fast as possible,
    // THEN thumbnails fill in across the whole library, THEN the much more
    // expensive filmstrips (up to 11 ffmpeg processes per clip) last. A
    // single clip's full pipeline no longer blocks every other clip behind
    // it in the list from getting at least its basic info quickly.
    private async Task HydrateLibraryClipsAsync(IReadOnlyList<ClipCardViewModel> clips, CancellationToken cancellationToken)
    {
        try
        {
            // Bar resets per phase (0/clips.Count each time, not a combined
            // 0/clips.Count*3) - RunHydrationPassAsync resets
            // HydrationCompleted/HydrationTotal (and _hydrationClock) at the
            // start of every pass, so a 300-clip library reads "0/300" for
            // each of the three phases in turn, not "0/900" for the whole
            // thing. _hydrationOverallCompleted/Total track the WHOLE job's
            // remaining-item count separately, unaffected by those per-phase
            // resets - the ETA is for the whole job finishing, not just
            // whichever phase happens to be running.
            // Only clips that are actually missing something get a pass over
            // them. CreateLibraryStub has already filled in everything the
            // caches on disk could answer (probed duration/tracks, thumbnail,
            // filmstrip), so on a library that hasn't changed since the last
            // run all three of these are empty and hydration is a no-op -
            // where it used to walk every clip, one at a time, three times,
            // re-reading caches to arrive back at what the cards were already
            // showing. That walk is what made a cold start crawl and put the
            // "Building your library" banner up on every single launch.
            var needProbe = clips.Where(clip => clip.Duration <= TimeSpan.Zero).ToArray();
            var needThumbnail = clips.Where(clip => string.IsNullOrEmpty(clip.Media.ThumbnailPath)).ToArray();
            var needFilmstrip = clips.Where(clip => string.IsNullOrEmpty(clip.Media.FilmstripPath)).ToArray();

            _hydrationOverallCompleted = 0;
            _hydrationOverallTotal = needProbe.Length + needThumbnail.Length + needFilmstrip.Length;
            var pending = _hydrationOverallTotal;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsHydratingLibrary = pending > 0;
                HydrationEtaText = string.Empty;
            });

            if (pending == 0)
            {
                AppLog.Info($"Library hydration: nothing to do, all {clips.Count} clips served from cache.");
                return;
            }

            AppLog.Info($"Library hydration: {needProbe.Length} to probe, {needThumbnail.Length} thumbnails, {needFilmstrip.Length} filmstrips (of {clips.Count} clips).");

            await RunHydrationPassAsync(needProbe, "Loading clip info", cancellationToken,
                async clip =>
                {
                    var media = await _mediaProbe.ProbeMetadataAsync(clip.Path);
                    await Dispatcher.UIThread.InvokeAsync(() => clip.UpdateMedia(media));
                });

            // Recomputed rather than reusing the list from above: a clip that
            // had no cached probe a moment ago has a real duration now, and
            // the thumbnail/filmstrip grabs seek by duration.
            needThumbnail = clips.Where(clip => string.IsNullOrEmpty(clip.Media.ThumbnailPath)).ToArray();
            await RunHydrationPassAsync(needThumbnail, "Loading thumbnails", cancellationToken,
                async clip =>
                {
                    var path = await _mediaProbe.EnsureThumbnailAsync(clip.Path, clip.Duration);
                    if (string.IsNullOrEmpty(path)) return;
                    await Dispatcher.UIThread.InvokeAsync(() => clip.UpdateMedia(clip.Media with { ThumbnailPath = path }));
                });

            needFilmstrip = clips.Where(clip => string.IsNullOrEmpty(clip.Media.FilmstripPath)).ToArray();
            await RunHydrationPassAsync(needFilmstrip, "Loading timelines", cancellationToken,
                async clip =>
                {
                    var path = await _mediaProbe.EnsureFilmstripAsync(clip.Path, clip.Duration);
                    if (string.IsNullOrEmpty(path)) return;
                    await Dispatcher.UIThread.InvokeAsync(() => clip.UpdateMedia(clip.Media with { FilmstripPath = path }));
                });
        }
        catch (OperationCanceledException)
        {
            // New folder/editor open superseded this scan.
        }
        finally
        {
            // Guarded the same way as the dispose below - a superseded scan's
            // finally still runs (just later, asynchronously), and without
            // this check it could clear IsHydratingLibrary out from under a
            // newer scan that's already replaced it and is still running.
            if (_libraryHydrationCts?.Token == cancellationToken)
            {
                _libraryHydrationCts.Dispose();
                _libraryHydrationCts = null;
                _hydrationClock.Stop();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsHydratingLibrary = false;
                    HydrationEtaText = string.Empty;
                });
            }
        }
    }

    // One pass of HydrateLibraryClipsAsync - runs `action` for every clip
    // (one at a time, matching the existing network-drive-friendly
    // MaxDegreeOfParallelism), resetting the progress bar to 0/clips.Count
    // for this phase specifically.
    private async Task RunHydrationPassAsync(
        IReadOnlyList<ClipCardViewModel> clips,
        string phaseLabel,
        CancellationToken cancellationToken,
        Func<ClipCardViewModel, Task> action)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            HydrationPhaseLabel = phaseLabel;
            HydrationCompleted = 0;
            HydrationTotal = clips.Count;
            // Restarted per-phase, not once for the whole job - ffprobe vs
            // thumbnail vs filmstrip generation cost wildly different
            // amounts, so a rate averaged since the job started stays
            // dragged toward whatever phase ran first for a long time after
            // a slower phase begins (the export/update dialogs' own ETAs
            // avoid exactly this by timing only the operation actually in
            // progress - see ExportButton_OnClick's encodeClock). Using the
            // CURRENT phase's own live rate, projected across however many
            // items are left in the WHOLE job, keeps the number both
            // accurate to what's actually running right now and still an
            // estimate for the whole job finishing.
            _hydrationClock.Restart();
        });

        await Parallel.ForEachAsync(
            clips,
            new ParallelOptions { MaxDegreeOfParallelism = 1, CancellationToken = cancellationToken },
            async (clip, token) =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    await action(clip);
                    if (token.IsCancellationRequested) return;
                    await Dispatcher.UIThread.InvokeAsync(RecordHydrationStep);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // A bad clip in one pass shouldn't stop the rest of the library.
                    await Dispatcher.UIThread.InvokeAsync(RecordHydrationStep);
                }
            });
    }

    // One clip finished in whichever pass is currently running - advances
    // both the displayed per-phase count AND the overall-job counter the ETA
    // is computed from.
    private void RecordHydrationStep()
    {
        HydrationCompleted++;
        _hydrationOverallCompleted++;
        UpdateHydrationEta();
    }

    // Straight-line estimate from the CURRENT phase's own live rate
    // (items/sec since _hydrationClock last restarted, at the start of
    // whichever pass is running now - same idea as the export/save-trim
    // dialogs' encodeClock, timing only the operation actually in
    // progress), projected across every item still left in the WHOLE job
    // (_hydrationOverallTotal/_hydrationOverallCompleted, unaffected by the
    // per-phase resets above). A couple of completed items is enough to
    // trust - HydrationCompleted itself already resets to 0 at the start of
    // each phase, so it doubles as "how many samples the current rate is
    // based on" without a separate counter.
    private void UpdateHydrationEta()
    {
        const int minSamplesForEstimate = 2;
        if (HydrationCompleted < minSamplesForEstimate || _hydrationOverallTotal <= 0)
        {
            HydrationEtaText = string.Empty;
            return;
        }

        var remaining = _hydrationOverallTotal - _hydrationOverallCompleted;
        if (remaining <= 0)
        {
            HydrationEtaText = string.Empty;
            return;
        }

        var secondsPerItem = _hydrationClock.Elapsed.TotalSeconds / HydrationCompleted;
        var etaSeconds = secondsPerItem * remaining;
        HydrationEtaText = FormatHydrationEta(etaSeconds);
    }

    // Same granular "Xs" / "Xm YYs" style as MainWindow.axaml.cs's FormatEta
    // (export/save-trim's own ETA), instead of the old "~Xs left" rounded up
    // to the nearest 5 seconds / whole minute - a live class here, not a
    // ViewModel dependency on View code-behind.
    private static string FormatHydrationEta(double etaSeconds)
    {
        if (etaSeconds < 1) return "less than a second left";
        if (etaSeconds < 60) return $"{etaSeconds:0}s left";
        var remaining = TimeSpan.FromSeconds(etaSeconds);
        return $"{(int)remaining.TotalMinutes}m {remaining.Seconds:00}s left";
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
        // TB included: a library past 1024 GB used to read "2048 GB", which is
        // both wrong-looking and too wide for the sidebar rail it's shown in.
        string[] units = { "B", "KB", "MB", "GB", "TB" };
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

    private static string FpsSuffix(double fps)
    {
        return fps > 0 ? $"@{Math.Round(fps):0}" : string.Empty;
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
