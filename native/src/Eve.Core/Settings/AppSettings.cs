using System.Text.Json.Serialization;

namespace Eve.Core.Settings;

public sealed class AppSettings
{
    public string LibraryFolder { get; set; } = string.Empty;
    // Standard preserves the filename layout used before filename schemes were
    // configurable.  The custom template is deliberately kept separately so a
    // user can switch presets without losing their work-in-progress template.
    public string ClipFileNameScheme { get; set; } = "Standard";
    public string CustomClipFileNameTemplate { get; set; } = "{datetime:yyyy-MM-dd HH-mm-ss} - {title}";
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
    public bool IsStatusAreaVisible { get; set; } = true;
    public bool ShowRecordingPausedIndicator { get; set; } = true;
    // On by default - MainWindowViewModel.UpdateCardLayout targets a fixed
    // card width and lets the column count itself grow on wider windows
    // (e.g. ~6 across on a 1440p-wide window) instead of always exactly 3
    // columns that just stretch wider.
    public bool ScaleClipsWithWindow { get; set; } = true;
    // Positive shifts audio EARLIER relative to video (fixes audio sounding
    // delayed/late); negative shifts audio LATER (fixes audio sounding
    // ahead). Exact WASAPI/hardware-encoder latency varies too much by
    // machine to hardcode a correction - OBS exposes the same kind of
    // manual per-source sync offset for this exact reason.
    public int AudioSyncOffsetMs { get; set; }
    public string IgnoredUpdateVersion { get; set; } = string.Empty;
    public string ChatAudioDeviceId { get; set; } = string.Empty;
    // Single-selection fields - still the persisted choice while the matching
    // Multi*Enabled toggle below is off, so most users (one mic, at most one
    // chat app) never need to touch the multi-select add/remove list at all.
    public string ChatAudioProcessName { get; set; } = string.Empty;
    public string MicrophoneDeviceId { get; set; } = "default";
    // Multi-select lists - only consulted when the matching toggle is on.
    public bool MultiChatAppEnabled { get; set; }
    public bool MultiMicrophoneEnabled { get; set; }
    public List<string> ChatAudioProcessNames { get; set; } = new();
    public List<string> MicrophoneDeviceIds { get; set; } = new();
    public bool MicrophoneNoiseSuppressionEnabled { get; set; }
    // ffmpeg afftdn's nr= (noise reduction) parameter, in dB - higher cuts more
    // noise but risks eating into speech. afftdn's own valid range is 0.01-97;
    // clamped tighter (0-30) at the settings-UI layer since anything past ~20
    // starts sounding artifacty on typical mic noise floors.
    public double MicrophoneNoiseSuppressionStrength { get; set; } = 12.0;
    public List<string> GameAudioExcludedProcesses { get; set; } = new();
    public bool EnableEditorKeyboardShortcuts { get; set; } = true;
    public string ClipOverlayPosition { get; set; } = "Top Right";
    public string ClipOverlayVolume { get; set; } = "Medium";
    // Editor's master output volume (fullscreen playbar slider) - separate
    // from TrackVolumes (per-clip, per-track mix levels stored in
    // ClipEditSettings), this is a single global preference like any media
    // player remembering your last volume across everything you play.
    public double EditorMasterVolume { get; set; } = 100;
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 780;
    public bool IsWindowMaximized { get; set; }
    public Dictionary<string, ClipEditSettings> ClipEdits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool EnableClipOverlay { get; set; } = true;
    public bool EnableClipOverlaySound { get; set; } = true;
    public List<GameCaptureOverride> GameCaptureOverrides { get; set; } = new();
    // Executables the user explicitly told game detection to skip (via the
    // detected-game header flyout or Settings > Game Detection) - on top of
    // the built-in ignored list, which covers common non-game apps but can
    // never cover everything.
    public List<string> IgnoredGameExecutables { get; set; } = new();
    public Cs2AutoClipSettings Cs2AutoClip { get; set; } = new();
    public bool MedalImportStripEmoji { get; set; } = false;
    public bool MedalImportCopyNotMove { get; set; } = true;
    // Read only to migrate older settings files. New import history is stored
    // with the library in .clipinfo/medal-imports.json instead of AppData.
    [JsonPropertyName("ImportedMedalClipKeys")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyImportedMedalClipKeys { get; set; }
    // One-time migration flag: existing clips sitting flat in the library
    // root get moved into per-game subfolders the first library refresh
    // after this shipped. False (unset) on any settings.json predating it.
    public bool ClipsMigratedToGameFolders { get; set; }
    public int LibraryLayoutVersion { get; set; }
    // Defaults true so upgrading an existing install (settings.json already exists,
    // this key just isn't in it yet) never shows the walkthrough - only a genuinely
    // fresh install (no settings.json at all, see AppSettingsStore.Load) gets it
    // explicitly set to false.
    public bool HasSeenOnboarding { get; set; } = true;
    // Off by default and requires an explicit destination folder - the EVE (Native)
    // backend's continuous encoder can also write the whole session to disk
    // alongside the rolling replay buffer, separate from clip saves. Native only
    // for now; Legacy/OBS would need their own, larger wiring.
    public bool FullSessionRecordingEnabled { get; set; }
    public string FullSessionRecordingFolder { get; set; } = string.Empty;
    // H.264 = mux the already-encoded stream as-is (fast, bigger file);
    // H.265/AV1 re-encode at finalize time via NVENC for smaller session files.
    public string FullSessionVideoCodec { get; set; } = "H.264";
    // 0 = unlimited. When set, the oldest EVE session recordings are deleted
    // after each save until the session folder fits the quota again.
    public int FullSessionQuotaGb { get; set; }
    // On: session video lands on disk the moment recording stops; audio
    // tracks are attached by a background job afterward (file is briefly
    // video-only). Off: the whole mux runs before the session file appears.
    public bool FullSessionBackgroundFinalize { get; set; } = true;
}

public sealed class ClipEditSettings
{
    public double TrimStartSeconds { get; set; }
    public double TrimEndSeconds { get; set; }
    public Dictionary<int, double> TrackVolumes { get; set; } = new();
}

public sealed class GameCaptureOverride
{
    public string ExecutableName { get; set; } = string.Empty;
    // Empty for a built-in catalog game (its name comes from the catalog);
    // set only for a user-added game the built-in catalog doesn't know about.
    public string DisplayName { get; set; } = string.Empty;
    public string CaptureBackend { get; set; } = "Auto";
}

public sealed class Cs2AutoClipSettings
{
    // On by default for fresh installs; an existing settings.json keeps
    // whatever the user last had (the stored value wins over this default).
    public bool Enabled { get; set; } = true;
    public bool Kill { get; set; }
    public bool TwoKill { get; set; }
    public bool ThreeKill { get; set; } = true;
    public bool FourKill { get; set; } = true;
    public bool Ace { get; set; } = true;
    public bool Headshot { get; set; }
    public bool Death { get; set; }
    public bool Assist { get; set; }
    public int GsiPort { get; set; } = 3499;
}
