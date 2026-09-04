using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

internal static class RecordingAudioProcessPolicy
{
    private static readonly string[] BlockedProcessNames = ["ClypDat", "ClypDatRecorder", "MedalEncoder"];

    internal static bool IsEligible(string? processName) =>
        !BlockedProcessNames.Any(blocked => AudioProcessIdentity.Equals(processName, blocked));

    internal static Dictionary<string, int> Filter(IReadOnlyDictionary<string, int> processes) =>
        processes.Where(pair => IsEligible(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}
