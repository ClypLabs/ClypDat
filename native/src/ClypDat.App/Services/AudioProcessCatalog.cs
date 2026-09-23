using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace ClypDat.App.Services;

public sealed record ActiveAudioProcess(string Name, int ProcessId, string ExecutablePath);

public static class AudioProcessCatalog
{
    internal static int[] ResolveActiveAudioProcessIds(MMDeviceEnumerator enumerator)
    {
        var ids = new HashSet<int>();
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (var index = 0; index < sessions.Count; index++)
            {
                using var session = sessions[index];
                try
                {
                    if (session.IsSystemSoundsSession) continue;
                    if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                    var processId = (int)session.GetProcessID;
                    if (processId <= 0) continue;
                    using var process = Process.GetProcessById(processId);
                    if (!process.HasExited) ids.Add(processId);
                }
                catch
                {
                    // Audio sessions can disappear while enumerating.
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Active audio process resolve failed", error);
        }

        return ids.ToArray();
    }

    public static IReadOnlyList<string> GetActiveAudioProcessNames()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        using var enumerator = new MMDeviceEnumerator();
        return ResolveActiveAudioProcessIds(enumerator)
            .Select(processId =>
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    return process.ProcessName;
                }
                catch { return string.Empty; }
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ActiveAudioProcess> GetActiveAudioProcesses()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ActiveAudioProcess>();
        using var enumerator = new MMDeviceEnumerator();
        return ResolveActiveAudioProcessIds(enumerator)
            .Select(processId =>
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    return new ActiveAudioProcess(process.ProcessName, processId, process.MainModule?.FileName ?? string.Empty);
                }
                catch { return null; }
            })
            .Where(process => process is not null && !string.IsNullOrWhiteSpace(process.Name))
            .Select(process => process!)
            .GroupBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(process => process.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
