using System.Text.Json.Serialization;

namespace ClypDat.Core.Settings;

public sealed class AppSettings
{
    public string LibraryFolder { get; set; } = string.Empty;
    // Which Settings nav section was open last, so reopening Settings picks
    // up where the user left off instead of always landing on the same page.
    public string LastSettingsSection { get; set; } = string.Empty;
    // 0 = no limit, same convention as FullSessionQuotaGb. A soft target the
    // sidebar's storage ring fills against and warns past - nothing deletes
    // clips to enforce it.
    public int LibraryStorageLimitGb { get; set; }
    // Standard preserves the filename layout used before filename schemes were
    // configurable.  The custom template is deliberately kept separately so a
    // user can switch presets without losing their work-in-progress template.
    public string ClipFileNameScheme { get; set; } = "Standard";
    public string CustomClipFileNameTemplate { get; set; } = "{datetime:yyyy-MM-dd HH-mm-ss} - {title}";
    // Master on/off for the rolling buffer. On by default (the buffer is
    // meant to be armed and forgotten); off means no game detection ever
    // starts a capture, so nothing is recorded until it's turned back on.
    public bool ReplayBufferEnabled { get; set; } = true;
    // "Game" keeps the existing detected-game workflow. "Desktop" keeps a
    // selected display armed even when no game is running.
    public string ReplayCaptureSource { get; set; } = "Game";
    // Win32 display device path, e.g. \\.\DISPLAY1. Empty means primary.
    public string ReplayDesktopMonitorDeviceName { get; set; } = string.Empty;
    public bool ReplayDesktopCaptureCursor { get; set; } = true;
    // When Desktop Capture is selected, temporarily capture a detected game
    // window instead. The desktop monitor remains the user's saved source and
    // resumes as soon as the game exits.
    public bool ReplayAutoSwitchToGameCapture { get; set; } = true;
    public int ReplayDurationSeconds { get; set; } = 60;
    // H.264 is default for widest playback compatibility. AV1Preferred uses
    // hardware AV1 when available, then falls back through H.264.
    public string ReplayVideoCodec { get; set; } = "H.264";
    public string ReplayEncoderMode { get; set; } = "GPU";
    // Sustained encoder overload may lower live target FPS, then restore it
    // after recovery. Users can disable this protection.
    public bool ReplayAdaptiveFrameRateEnabled { get; set; } = true;
    // VFR follows the capture clock and avoids spending encoder work on
    // duplicate frames. CFR remains available for applications that require
    // a fixed frame grid.
    public string ReplayFrameRateMode { get; set; } = "VFR";
    // Existing settings files need one-time migration from old Auto default.
    public bool ReplayH264DefaultApplied { get; set; }
    // Fixed CBR target for every replay backend. Persisted range: 5-100 Mbps.
    public int ReplayBitrateMbps { get; set; } = 15;
    // Existing installs that received the old 40 Mbps default are switched to
    // the current 15 Mbps default once; later user choices are preserved.
    public bool ReplayBitrateDefault15Applied { get; set; }
    public int ReplayFrameRate { get; set; } = 60;
    public int ReplayMaxHeight { get; set; } = 1080;
    public string ReplayBackend { get; set; } = "Auto";
    public string ExportVideoCodec { get; set; } = "H.264";
    public string SaveReplayHotkey { get; set; } = "Ctrl+Shift+F9";
    public bool StartReplayOnLaunch { get; set; }
    public bool LaunchOnWindowsStartup { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public bool AutomaticallyFocusOnGameExit { get; set; }
    // Windows process priority for ClypDat's in-process capture backends.
    // Normal by default: High lets capture win CPU against the game, but it
    // also lets the save/hydration pipeline outrank the game and the desktop
    // compositor, which reads as system-wide stutter on every clip save.
    // Raise it manually if capture drops frames under load.
    public string ProcessPriority { get; set; } = "Normal";
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
    // machine to hardcode a correction.
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
    public List<string> GameAudioExcludedProcesses { get; set; } = new();
    public bool EnableEditorKeyboardShortcuts { get; set; } = true;
    // On: the editor's playback controls are the floating bar that follows the
    // pointer over the picture. Off: they're docked under the video, always
    // there. The floating bar is the default - it keeps the picture clear
    // whenever the pointer isn't on it, and it is what players expect from a
    // video view. Docked stays available for anyone who would rather the
    // controls never cover a frame and never move.
    public bool EditorHoverBarEnabled { get; set; } = true;
    public bool EnableClipHoverPreview { get; set; } = true;
    // Existing installs already have EditorHoverBarEnabled written to their
    // settings.json as false, so flipping the default above reaches new
    // installs only. See AppSettingsStore for the one-time switch-on; this
    // guards it from fighting anyone who turns the bar back off afterward.
    public bool HoverBarDefaultOnApplied { get; set; }
    // Off: exactly one sidebar filter is ever active - picking a game drops
    // the section you were in and vice versa. On: one of each can be held at
    // the same time ("Fortnite" + "Auto-Clips"). Either way the rail stays
    // single-select within each group; stacking several games at once is what
    // the Game Filters dropdown is for.
    public bool CombineSidebarFilters { get; set; }
    // User-chosen display names for games, keyed by the name the library
    // resolved on its own (an executable name for a game the catalog doesn't
    // know, a Medal folder name, a detected title). Applied wherever a game
    // name is read, so a rename follows through to the sidebar, the filters,
    // the Library heading and the key its icon is looked up under - without
    // touching a single file on disk.
    public Dictionary<string, string> GameDisplayNameOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Sidebar game-rail organisation, both empty until the user actually
    // touches it (drags something, or uses a "Move to folder"/"New Folder"
    // menu action) - see MainWindowViewModel.RebuildGameRail. Empty means the
    // rail still uses its automatic layout: most-clipped games inline, the
    // rest folded into an unnamed "More Games" folder. Touching it converts
    // that automatic folder into a real one and locks in an explicit order
    // from that point on.
    public List<GameRailFolder> GameRailFolders { get; set; } = new();
    // Top-level rail order once customised - each entry is "game:<key>" or
    // "folder:<id>". A game/folder that exists but isn't listed here (a new
    // game that showed up after the last customisation) is appended to the
    // rail rather than hidden.
    public List<string> GameRailOrder { get; set; } = new();
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
    // Keeps clip-notification overlays on the physical screen but out of
    // anything that captures it - ClypDat's own clips, Discord screenshare. Same
    // mechanism KeePassXC uses to keep its window out of screenshots
    // (SetWindowDisplayAffinity/WDA_EXCLUDEFROMCAPTURE). On by default: a
    // "Clip saved" badge belongs to the person at the desk, not baked into
    // the clip they are saving. Needs Windows 10 2004 (build 19041); below
    // that the call fails and notifications simply capture as before. Editor
    // controls and playback-state UI intentionally remain capturable.
    //
    // Convenience, not a security boundary - it only stops capture that goes
    // through DWM. A capture card, a second PC, or a phone pointed at the
    // monitor all still see it.
    public bool ExcludeOverlaysFromCapture { get; set; } = true;
    // Summary popup when a game closes, listing what that session actually
    // captured. Without it the only way to find out is to go and look in the
    // library afterwards, which is easy to forget doing.
    public bool ShowNewClipsOnGameClose { get; set; } = true;
    // Separate from EnableClipOverlay - "clipping started" is a distinct
    // notification kind (fires when the buffer actually starts recording a
    // detected game) from the clip-saved family, and a user may want one
    // without the other.
    public bool EnableGameDetectedOverlay { get; set; } = true;
    // "Auto clip started - X detected, ..." - fires the instant a GSI
    // listener (CS2/Dota/League) spots a highlight-worthy event, well before
    // the clip itself actually saves. Distinct concern from EnableClipOverlay
    // (which covers the save lifecycle itself): this is about event
    // detection, not file save mechanics.
    public bool EnableAutoClipPendingOverlay { get; set; } = true;
    // "Auto clip failed" - distinct from EnableClipOverlay's success-path
    // notifications so a user can keep failure alerts even with save
    // confirmations off, or vice versa.
    public bool EnableAutoClipFailedOverlay { get; set; } = true;
    public List<GameCaptureOverride> GameCaptureOverrides { get; set; } = new();
    // Executables user explicitly told game detection to skip.
    public List<string> IgnoredGameExecutables { get; set; } = new();
    // Auto-clipping is data-driven so new game providers do not need their own
    // one-off settings model. Cs2AutoClip remains below solely to migrate older
    // settings files without losing a user's existing selections.
    public AutoClippingSettings AutoClipping { get; set; } = new();
    public Cs2AutoClipSettings Cs2AutoClip { get; set; } = new();
    public bool MedalImportStripEmoji { get; set; } = false;
    public bool MedalImportCopyNotMove { get; set; } = true;
    public bool SteelSeriesImportCopyNotMove { get; set; } = true;
    // Suppresses the startup prompt for audio-only MP4 sidecars. ClypDat
    // still keeps them in the library; it only stops asking whether to remove
    // them.
    public bool IgnoreAudioOnlyClipPrompt { get; set; }
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
    // Off by default and requires an explicit destination folder - the ClypDat (Native)
    // backend's continuous encoder can also write the whole session to disk
    // alongside the rolling replay buffer, separate from clip saves. Native only
    // for now; Legacy would need its own, larger wiring.
    public bool FullSessionRecordingEnabled { get; set; }
    public string FullSessionRecordingFolder { get; set; } = string.Empty;
    // H.264 = mux the already-encoded stream as-is (fast, bigger file);
    // AV1 re-encodes at finalize time via NVENC for smaller session files.
    public string FullSessionVideoCodec { get; set; } = "H.264";
    // 0 = unlimited. When set, the oldest ClypDat session recordings are deleted
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
    // The editor has had a Description box since before this field existed,
    // bound to a view-model property nothing ever read or persisted - typing in
    // it and closing the editor silently threw the text away. Sidecars written
    // before this simply deserialize it as empty.
    public string Description { get; set; } = string.Empty;
}

public sealed class GameCaptureOverride
{
    // The detection key, not necessarily a real filename - for Catalog-origin
    // rows this is "steam-{appid}" / "epic-{slug}" / etc (see
    // ForegroundGameDetector), so it must stay stable: it's the identity used
    // for dedupe, removal, the ignore list, and per-game backend lookup.
    public string ExecutableName { get; set; } = string.Empty;
    // Empty when row only stores capture-backend choice.
    public string DisplayName { get; set; } = string.Empty;
    // The actual exe filename (e.g. "Overwatch.exe"), separate from
    // ExecutableName above - Game Detection's UI shows this as the row's
    // subtitle. Rows saved before this field existed have it empty until the
    // game is next detected.
    public string ProcessName { get; set; } = string.Empty;
    public string CaptureBackend { get; set; } = "Auto";
    // "Catalog" rows only remember a capture-backend choice for a game found
    // by the shared catalog or Steam manifest. They must not turn into a
    // process-only detection rule on later launches.
    public string? Origin { get; set; }
}

public sealed class GameRailFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Folder";
    // Game display names, in the order they render inside the folder. Not
    // pruned when a game's clips are all gone - if it comes back, it lands
    // straight back where the user put it instead of at the end.
    public List<string> GameKeys { get; set; } = new();
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

public sealed class AutoClippingSettings
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, AutoClipGameSettings> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AutoClipGameSettings
{
    public bool Enabled { get; set; } = true;
    // Deathmatch has frequent respawns, so keep its auto-clips opt-in.
    // Missing values from existing settings.json files deserialize as false.
    public bool DeathmatchClipping { get; set; }
    public int ListenerPort { get; set; }
    public Dictionary<string, bool> Events { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
