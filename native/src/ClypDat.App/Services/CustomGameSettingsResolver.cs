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
    public const string QualityGroup = "Quality";
    public const string ReplayGroup = "Replay";
    public const string AudioGroup = "Audio";
    public const string FullSessionGroup = "FullSession";

    public static readonly IReadOnlyList<string> AllGroups = new[]
    {
        QualityGroup, ReplayGroup, AudioGroup, FullSessionGroup
    };

    public static string GroupDisplayName(string group) => group switch
    {
        QualityGroup => "Recording Quality",
        ReplayGroup => "Replay Length and Hotkey",
        AudioGroup => "Audio",
        FullSessionGroup => "Full Session Recording",
        _ => group
    };

    public static string GroupDescription(string group) => group switch
    {
        QualityGroup => "Codec, encoder, bitrate, frame rate and resolution cap for this game.",
        ReplayGroup => "How much of this game the buffer keeps, and the key that saves it.",
        AudioGroup => "Game and microphone levels, and microphone noise suppression.",
        FullSessionGroup => "Whether whole sessions of this game are recorded, and at what quality.",
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
                profile.GameAudioVolumePercent = settings.GameAudioVolumePercent;
                profile.MicrophoneVolumePercent = settings.MicrophoneVolumePercent;
                profile.MicrophoneNoiseSuppressionEnabled = settings.MicrophoneNoiseSuppressionEnabled;
                profile.MicrophoneNoiseGateThresholdDb = settings.MicrophoneNoiseGateThresholdDb;
                break;

            case FullSessionGroup:
                profile.FullSessionRecordingEnabled = settings.FullSessionRecordingEnabled;
                profile.FullSessionVideoCodec = settings.FullSessionVideoCodec;
                profile.FullSessionQuotaGb = settings.FullSessionQuotaGb;
                break;
        }
    }

    /// <summary>Every overridable value for one game, already resolved.</summary>
    public static EffectiveRecordingSettings Resolve(AppSettings settings, string? detectionKey)
    {
        var quality = FindActive(settings, detectionKey, QualityGroup);
        var replay = FindActive(settings, detectionKey, ReplayGroup);
        var audio = FindActive(settings, detectionKey, AudioGroup);
        var session = FindActive(settings, detectionKey, FullSessionGroup);

        return new EffectiveRecordingSettings(
            ReplayVideoCodec: quality?.ReplayVideoCodec ?? settings.ReplayVideoCodec,
            ReplayEncoderMode: quality?.ReplayEncoderMode ?? settings.ReplayEncoderMode,
            ReplayBitrateMbps: quality?.ReplayBitrateMbps ?? settings.ReplayBitrateMbps,
            ReplayFrameRate: quality?.ReplayFrameRate ?? settings.ReplayFrameRate,
            ReplayMaxHeight: quality?.ReplayMaxHeight ?? settings.ReplayMaxHeight,
            ReplayFrameRateMode: quality?.ReplayFrameRateMode ?? settings.ReplayFrameRateMode,
            ReplayDurationSeconds: replay?.ReplayDurationSeconds ?? settings.ReplayDurationSeconds,
            SaveReplayHotkey: replay?.SaveReplayHotkey ?? settings.SaveReplayHotkey,
            GameAudioVolumePercent: audio?.GameAudioVolumePercent ?? settings.GameAudioVolumePercent,
            MicrophoneVolumePercent: audio?.MicrophoneVolumePercent ?? settings.MicrophoneVolumePercent,
            MicrophoneNoiseSuppressionEnabled: audio?.MicrophoneNoiseSuppressionEnabled ?? settings.MicrophoneNoiseSuppressionEnabled,
            MicrophoneNoiseGateThresholdDb: audio?.MicrophoneNoiseGateThresholdDb ?? settings.MicrophoneNoiseGateThresholdDb,
            FullSessionRecordingEnabled: session?.FullSessionRecordingEnabled ?? settings.FullSessionRecordingEnabled,
            FullSessionVideoCodec: session?.FullSessionVideoCodec ?? settings.FullSessionVideoCodec,
            FullSessionQuotaGb: session?.FullSessionQuotaGb ?? settings.FullSessionQuotaGb,
            AppliedGroups: DescribeAppliedGroups(quality, replay, audio, session));
    }

    // Purely for the log line at the start of a recording - "which of these
    // settings are not the ones in the settings page?" is otherwise an
    // unanswerable question when a user reports odd output for one game.
    private static string DescribeAppliedGroups(
        CustomGameProfile? quality, CustomGameProfile? replay, CustomGameProfile? audio, CustomGameProfile? session)
    {
        var applied = new List<string>(4);
        if (quality is not null) applied.Add(QualityGroup);
        if (replay is not null) applied.Add(ReplayGroup);
        if (audio is not null) applied.Add(AudioGroup);
        if (session is not null) applied.Add(FullSessionGroup);
        return applied.Count == 0 ? string.Empty : string.Join(",", applied);
    }
}

internal sealed record EffectiveRecordingSettings(
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
    string AppliedGroups);
