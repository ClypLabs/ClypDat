namespace Eve.Core.Settings;

public sealed class AppSettings
{
    public string LibraryFolder { get; set; } = string.Empty;
    public int ReplayDurationSeconds { get; set; } = 60;
    public string ReplayQualityPreset { get; set; } = "Balanced";
    public int ReplayFrameRate { get; set; } = 60;
    public int ReplayMaxHeight { get; set; } = 1080;
    public string ReplayBackend { get; set; } = "Auto";
    public string ExportVideoCodec { get; set; } = "H.264";
    public string SaveReplayHotkey { get; set; } = "Ctrl+Shift+F9";
    public bool StartReplayOnLaunch { get; set; }
    public bool LaunchOnWindowsStartup { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public string IgnoredUpdateVersion { get; set; } = string.Empty;
    public string ChatAudioDeviceId { get; set; } = string.Empty;
    public string ChatAudioProcessName { get; set; } = string.Empty;
    public string MicrophoneDeviceId { get; set; } = "default";
    public List<string> GameAudioExcludedProcesses { get; set; } = new();
    public bool EnableEditorKeyboardShortcuts { get; set; } = true;
    public bool EnableClipHoverPreview { get; set; } = true;
    public string ClipOverlayPosition { get; set; } = "Top Right";
    public string ClipOverlayVolume { get; set; } = "Medium";
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 780;
    public bool IsWindowMaximized { get; set; }
    public Dictionary<string, ClipEditSettings> ClipEdits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ClipEditSettings
{
    public double TrimStartSeconds { get; set; }
    public double TrimEndSeconds { get; set; }
    public Dictionary<int, double> TrackVolumes { get; set; } = new();
}
