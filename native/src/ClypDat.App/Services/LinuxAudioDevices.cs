using System.Text.Json;

namespace ClypDat.App.Services;

internal static class LinuxAudioDevices
{
    internal static string? DefaultCaptureName()
    {
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var id = LinuxMediaProcess.RunAsync("pactl", ["get-default-source"], timeout.Token).GetAwaiter().GetResult().Trim();
            return GetDevices(true).FirstOrDefault(d => d.Id == id)?.Name;
        } catch (Exception error) { AppLog.Debug("Default microphone unavailable: " + error.Message); return null; }
    }
    internal static IReadOnlyList<AudioDeviceOption> GetDevices(bool capture)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var json = JsonDocument.Parse(LinuxMediaProcess.RunAsync("pactl", ["--format=json", "list", capture ? "sources" : "sinks"], timeout.Token).GetAwaiter().GetResult());
            return json.RootElement.EnumerateArray().Where(e => !capture || !e.GetProperty("name").GetString()!.EndsWith(".monitor", StringComparison.Ordinal))
                .Select(e => new AudioDeviceOption(e.GetProperty("name").GetString()!, e.GetProperty("description").GetString()!)).ToArray();
        }
        catch (Exception error) { AppLog.Debug("PipeWire audio devices unavailable: " + error.Message); return []; }
    }
    internal static IReadOnlyList<ActiveAudioProcess> GetApplications()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var json = JsonDocument.Parse(LinuxMediaProcess.RunAsync("pactl", ["--format=json", "list", "sink-inputs"], timeout.Token).GetAwaiter().GetResult());
            return json.RootElement.EnumerateArray().Select(e => e.GetProperty("properties"))
                .Where(p => p.TryGetProperty("application.process.id", out _) && p.TryGetProperty("application.process.binary", out _))
                .Select(p => int.TryParse(p.GetProperty("application.process.id").GetString(), out var pid)
                    ? new ActiveAudioProcess(p.GetProperty("application.process.binary").GetString()!, pid, LinuxProcessIdentity.Read(pid)?.ExecutablePath ?? "") : null)
                .OfType<ActiveAudioProcess>().DistinctBy(p => p.Name).ToArray();
        }
        catch (Exception error) { AppLog.Debug("PipeWire applications unavailable: " + error.Message); return []; }
    }
}
