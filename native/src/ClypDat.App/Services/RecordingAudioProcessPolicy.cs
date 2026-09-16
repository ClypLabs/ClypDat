using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

internal static class RecordingAudioProcessPolicy
{
    private static readonly string[] BlockedProcessNames = ["ClypDat", "ClypDatRecorder", "MedalEncoder"];

    // Virtual audio devices are the routing layer, not an app making sound.
    // Voicemeeter holds a session for every stream passing through it, so it
    // offered to record a mix of everything the machine plays - including the
    // game and microphone ClypDat already captures as their own tracks. Anyone
    // who switched it on got their whole desktop a second time, phase-aligned
    // with itself.
    //
    // Matched by prefix because one installer ships voicemeeter.exe,
    // voicemeeter8x64.exe, voicemeeterpro.exe and the Banana/Potato builds.
    private static readonly string[] BlockedProcessPrefixes = ["voicemeeter"];

    internal static bool IsEligible(string? processName) =>
        !BlockedProcessNames.Any(blocked => AudioProcessIdentity.Equals(processName, blocked)) &&
        !BlockedProcessPrefixes.Any(prefix =>
            Path.GetFileNameWithoutExtension(processName ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal static Dictionary<string, int> Filter(IReadOnlyDictionary<string, int> processes) =>
        processes.Where(pair => IsEligible(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}
