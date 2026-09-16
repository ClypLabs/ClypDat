using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

internal static class RecordingAudioProcessPolicy
{
    private static readonly string[] BlockedProcessNames = ["ClypDat", "ClypDatRecorder", "MedalEncoder"];

    // Two kinds of process hold an audio session without being something a
    // viewer would ever want on its own track.
    //
    // Voicemeeter is the routing layer rather than an app making sound: it
    // holds a session for every stream passing through it, so it offered to
    // record a mix of everything the machine plays, including the game and
    // microphone ClypDat already captures separately. Anyone who switched it on
    // got their whole desktop a second time, phase-aligned with itself.
    //
    // SignalRGB keeps a session open to drive lighting from audio levels. It
    // never plays anything, so the track it offers is a silent one, sitting in
    // the list between the apps that do.
    //
    // Matched by prefix because these ship under several executable names -
    // voicemeeter.exe, voicemeeter8x64.exe, voicemeeterpro.exe and the
    // Banana/Potato builds; SignalRgb.exe and SignalRgbLauncher.exe.
    private static readonly string[] BlockedProcessPrefixes = ["voicemeeter", "signalrgb"];

    internal static bool IsEligible(string? processName) =>
        !BlockedProcessNames.Any(blocked => AudioProcessIdentity.Equals(processName, blocked)) &&
        !BlockedProcessPrefixes.Any(prefix =>
            Path.GetFileNameWithoutExtension(processName ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal static Dictionary<string, int> Filter(IReadOnlyDictionary<string, int> processes) =>
        processes.Where(pair => IsEligible(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}
