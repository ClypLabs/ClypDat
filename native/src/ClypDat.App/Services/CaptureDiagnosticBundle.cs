using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

public static class CaptureDiagnosticBundle
{
    public static string Create(IReplayBuffer? replayBuffer)
    {
        Directory.CreateDirectory(AppLog.LogFolder);
        var path = Path.Combine(AppLog.LogFolder, $"clypdat-capture-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        var health = replayBuffer is IReplayCaptureDiagnostics diagnostics
            ? diagnostics.GetHealthSnapshot()
            : ReplayCaptureHealth.Unknown();
        WriteJson(archive, "capture-health.json", health);
        WriteJson(archive, "environment.json", new
        {
            os = RuntimeInformation.OSDescription,
            osVersion = Environment.OSVersion.VersionString,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            utc = DateTime.UtcNow
        });

        var logs = Directory.EnumerateFiles(AppLog.LogFolder, "clypdat*.log")
            .Concat(Directory.EnumerateFiles(AppLog.LogFolder, "obs-bridge.log"));
        foreach (var log in logs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = archive.CreateEntry($"logs/{Path.GetFileName(log)}", CompressionLevel.Optimal);
            using var output = new StreamWriter(entry.Open());
            output.Write(Scrub(File.ReadAllText(log)));
        }

        AppLog.Info($"Capture diagnostic bundle created: {path}.");
        return path;
    }

    private static void WriteJson<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Scrub(string value)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) value = value.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Environment.UserName)) value = value.Replace(Environment.UserName, "%USERNAME%", StringComparison.OrdinalIgnoreCase);
        return value;
    }
}
