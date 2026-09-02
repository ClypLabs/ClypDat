using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

// Per-game recording overrides: the one place that decides whether a game's own
// value or the global one is used.
//
// Shape borrowed from what Medal does, because it is the right shape: overrides
// are offered as GROUPS rather than as individual settings, and a group is
// seeded from the user's current global values when it is switched on. Two
// reasons that matters. A half-specified quality override (a codec without a
// bitrate) produces combinations nobody chose, and a user turning on "quality
// for Fortnite" means "start from what I already have and let me change it",
// not "reset this game to the factory defaults".
//
// Absence is the fallback. A game with no profile, or a profile with the group
// switched off, reads the global setting - there is no third "inherit" state to
// keep in sync.
internal static class CustomGameSettingsResolver
{
    // Group ids. Persisted inside CustomGameProfile.Groups, so treat them as a
    // wire format: renaming one silently drops every user's overrides for it.
    public const string RecordingModeGroup = "RecordingMode";
    public const string QualityGroup = "Quality";
    public const string ReplayGroup = "Replay";
    public const string AudioGroup = "Audio";
    public const string CaptureMethodGroup = "CaptureMethod";

    public static readonly IReadOnlyList<string> AllGroups = new[]
    {
        RecordingModeGroup, QualityGroup, ReplayGroup, AudioGroup, CaptureMethodGroup
    };

    // The one group a newly added game starts with. Adding a game is almost
    // always "record this one differently", and how it records at all is the
    // first question - the rest stay off so nothing else changes silently.
    public const string DefaultGroup = RecordingModeGroup;

    public const string ManualMode = "Manual";
    public const string FullSessionMode = "FullSession";
    public const string OffMode = "Off";

    public static string GroupDisplayName(string group) => group switch
    {
        RecordingModeGroup => "Recording Mode",
        QualityGroup => "Recording Quality",
        ReplayGroup => "Replay Length",
        AudioGroup => "Audio",
        CaptureMethodGroup => "Capture Method",
        _ => group
    };

    public static string GroupDescription(string group) => group switch
    {
        RecordingModeGroup => "Whether this game records by hotkey, records whole sessions, or is not recorded.",
        QualityGroup => "Codec, encoder, bitrate, frame rate and resolution cap for this game.",
        ReplayGroup => "How much of this game the replay buffer keeps.",
        AudioGroup => "Game and microphone levels, and microphone noise suppression.",
        CaptureMethodGroup => "Choose display capture or the experimental D3D11 graphics hook.",
        _ => string.Empty
    };

    /// <summary>
    /// The profile that applies to a detection key, or null when the game has
    /// none. Callers should treat null as "use the global settings".
    /// </summary>
    public static CustomGameProfile? Find(AppSettings settings, string? detectionKey)
    {
        if (string.IsNullOrWhiteSpace(detectionKey)) return null;
        if (settings.CustomGameSettings.TryGetValue(detectionKey, out var profile)) return profile;

        // One game can be known under more than one detection key - Rainbow Six
        // is both "steam-359550" and "RainbowSix.exe" in a real settings file,
        // and which one a session reports depends on how it was matched that
        // launch. The profile is stored against whichever key the picker
        // offered, so a direct miss falls back to "same game by name", or the
        // override would silently do nothing on exactly the launches that
        // matched the other way.
        var name = settings.GameCaptureOverrides
            .FirstOrDefault(game => string.Equals(game.ExecutableName, detectionKey, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var alias = settings.GameCaptureOverrides
            .Where(game => string.Equals(game.DisplayName, name, StringComparison.OrdinalIgnoreCase))
            .Select(game => game.ExecutableName)
            .FirstOrDefault(key => settings.CustomGameSettings.ContainsKey(key));

        return alias is not null ? settings.CustomGameSettings[alias] : null;
    }

    /// <summary>
    /// The profile that applies to a detection key ONLY if the named group is
    /// switched on for it. This is what every read site should use - asking for
    /// the profile without checking the group is how a disabled group's stale
    /// values leak into a recording.
    /// </summary>
    public static CustomGameProfile? FindActive(AppSettings settings, string? detectionKey, string group)
    {
        var profile = Find(settings, detectionKey);
        return profile is not null && HasGroup(profile, group) ? profile : null;
    }

    public static bool HasGroup(CustomGameProfile profile, string group) =>
        profile.Groups.Contains(group, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copies the user's current global values for a group into the profile.
    /// Called when a group is switched on, so the override starts as an exact
    /// copy of what the game was already recording with.
    /// </summary>
    public static void SeedGroupFromGlobal(AppSettings settings, CustomGameProfile profile, string group)
    {
        switch (group)
        {
            case RecordingModeGroup:
                // Derived, because there is no single global setting that says
                // "recording mode" - it is the combination of the buffer being
                // armed and full-session recording being on.
                profile.RecordingMode = !settings.ReplayBufferEnabled
                    ? OffMode
                    : settings.FullSessionRecordingEnabled ? FullSessionMode : ManualMode;
                break;

            case QualityGroup:
                profile.ReplayVideoCodec = settings.ReplayVideoCodec;
                profile.ReplayEncoderMode = settings.ReplayEncoderMode;
                profile.ReplayBitrateMbps = settings.ReplayBitrateMbps;
                profile.ReplayFrameRate = settings.ReplayFrameRate;
                profile.ReplayMaxHeight = settings.ReplayMaxHeight;
                profile.ReplayFrameRateMode = settings.ReplayFrameRateMode;
                break;

            case ReplayGroup:
                profile.ReplayDurationSeconds = settings.ReplayDurationSeconds;
                profile.SaveReplayHotkey = settings.SaveReplayHotkey;
                break;

            case AudioGroup:
                profile.AdditionalAudioProcesses = new Dictionary<string, int>(settings.AdditionalAudioProcesses, StringComparer.OrdinalIgnoreCase);
                profile.GameAudioVolumePercent = settings.GameAudioVolumePercent;
                profile.MicrophoneVolumePercent = settings.MicrophoneVolumePercent;
                profile.MicrophoneNoiseSuppressionEnabled = settings.MicrophoneNoiseSuppressionEnabled;
                profile.MicrophoneNoiseGateThresholdDb = settings.MicrophoneNoiseGateThresholdDb;
                break;

            case CaptureMethodGroup:
                profile.GameCaptureMethod = "Display";
                break;
        }
    }

    /// <summary>Every overridable value for one game, already resolved.</summary>
    public static EffectiveRecordingSettings Resolve(AppSettings settings, string? detectionKey)
    {
        var mode = FindActive(settings, detectionKey, RecordingModeGroup);
        var quality = FindActive(settings, detectionKey, QualityGroup);
        var replay = FindActive(settings, detectionKey, ReplayGroup);
        var audio = FindActive(settings, detectionKey, AudioGroup);
        var captureMethod = FindActive(settings, detectionKey, CaptureMethodGroup);

        // Recording Mode is the sole owner of whether this game records at all
        // and whether it records whole sessions. There was briefly a separate
        // Full Session group as well, which meant two groups owning one flag
        // and the last one edited winning - not something a settings page can
        // explain. How a session is recorded (codec, quota) stays global.
        var recordingEnabled = mode is null || !string.Equals(mode.RecordingMode, OffMode, StringComparison.OrdinalIgnoreCase);
        var fullSession = mode is not null
            ? string.Equals(mode.RecordingMode, FullSessionMode, StringComparison.OrdinalIgnoreCase)
            : settings.FullSessionRecordingEnabled;

        return new EffectiveRecordingSettings(
            RecordingEnabled: recordingEnabled,
            ReplayVideoCodec: quality?.ReplayVideoCodec ?? settings.ReplayVideoCodec,
            ReplayEncoderMode: quality?.ReplayEncoderMode ?? settings.ReplayEncoderMode,
            ReplayBitrateMbps: quality?.ReplayBitrateMbps ?? settings.ReplayBitrateMbps,
            ReplayFrameRate: quality?.ReplayFrameRate ?? settings.ReplayFrameRate,
            ReplayMaxHeight: quality?.ReplayMaxHeight ?? settings.ReplayMaxHeight,
            ReplayFrameRateMode: quality?.ReplayFrameRateMode ?? settings.ReplayFrameRateMode,
            ReplayDurationSeconds: replay?.ReplayDurationSeconds ?? settings.ReplayDurationSeconds,
            SaveReplayHotkey: replay?.SaveReplayHotkey ?? settings.SaveReplayHotkey,
            AdditionalAudioProcesses: audio?.AdditionalAudioProcesses ?? settings.AdditionalAudioProcesses,
            GameAudioVolumePercent: audio?.GameAudioVolumePercent ?? settings.GameAudioVolumePercent,
            MicrophoneVolumePercent: audio?.MicrophoneVolumePercent ?? settings.MicrophoneVolumePercent,
            // Noise suppression is deliberately NOT overridable per game right
            // now - its two controls were pulled from the Audio card. The
            // profile still carries and seeds the fields so restoring them is
            // just the card markup, but nothing may READ them while there is
            // no way to see or change what is stored: a value left behind from
            // when the controls existed would otherwise keep applying to a
            // game with nothing in the UI to explain it.
            MicrophoneNoiseSuppressionEnabled: settings.MicrophoneNoiseSuppressionEnabled,
            MicrophoneNoiseGateThresholdDb: settings.MicrophoneNoiseGateThresholdDb,
            FullSessionRecordingEnabled: recordingEnabled && fullSession,
            FullSessionVideoCodec: settings.FullSessionVideoCodec,
            FullSessionQuotaGb: settings.FullSessionQuotaGb,
            GameCaptureMethod: NormalizeGameCaptureMethod(captureMethod?.GameCaptureMethod),
            AppliedGroups: DescribeAppliedGroups(mode, quality, replay, audio, captureMethod));
    }

    public static string NormalizeGameCaptureMethod(string? value) =>
        string.Equals(value, "Hook", StringComparison.OrdinalIgnoreCase) ? "Hook" : "Display";

    // Purely for the log line at the start of a recording - "which of these
    // settings are not the ones in the settings page?" is otherwise an
    // unanswerable question when a user reports odd output for one game.
    private static string DescribeAppliedGroups(
        CustomGameProfile? mode, CustomGameProfile? quality, CustomGameProfile? replay, CustomGameProfile? audio,
        CustomGameProfile? captureMethod)
    {
        var applied = new List<string>(5);
        if (mode is not null) applied.Add(RecordingModeGroup);
        if (quality is not null) applied.Add(QualityGroup);
        if (replay is not null) applied.Add(ReplayGroup);
        if (audio is not null) applied.Add(AudioGroup);
        if (captureMethod is not null) applied.Add(CaptureMethodGroup);
        return applied.Count == 0 ? string.Empty : string.Join(",", applied);
    }
}

internal sealed record EffectiveRecordingSettings(
    bool RecordingEnabled,
    IReadOnlyDictionary<string, int> AdditionalAudioProcesses,
    string ReplayVideoCodec,
    string ReplayEncoderMode,
    int ReplayBitrateMbps,
    int ReplayFrameRate,
    int ReplayMaxHeight,
    string ReplayFrameRateMode,
    int ReplayDurationSeconds,
    string SaveReplayHotkey,
    int GameAudioVolumePercent,
    int MicrophoneVolumePercent,
    bool MicrophoneNoiseSuppressionEnabled,
    double MicrophoneNoiseGateThresholdDb,
    bool FullSessionRecordingEnabled,
    string FullSessionVideoCodec,
    int FullSessionQuotaGb,
    string GameCaptureMethod,
    string AppliedGroups);
