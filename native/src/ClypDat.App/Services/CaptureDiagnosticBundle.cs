using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

public static class CaptureDiagnosticBundle
{
    public static string Create(IReplayBuffer? replayBuffer, Cs2GsiListener? cs2GsiListener = null)
        => Create(replayBuffer, cs2GsiListener, AppLog.LogFolder, DateTime.Now);

    internal static string Create(
        IReplayBuffer? replayBuffer,
        Cs2GsiListener? cs2GsiListener,
        string logFolder,
        DateTime now)
    {
        Directory.CreateDirectory(logFolder);
        var path = Path.Combine(logFolder, $"clypdat-capture-diagnostics-{now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.zip");
        var partialPath = $"{path}.partial";

        try
        {
            using (var archive = ZipFile.Open(partialPath, ZipArchiveMode.Create))
            {
                var health = replayBuffer is IReplayCaptureDiagnostics diagnostics
                    ? diagnostics.GetHealthSnapshot()
                    : ReplayCaptureHealth.Unknown();
                WriteJson(archive, "capture-health.json", health);
                if (cs2GsiListener is not null) WriteJson(archive, "auto-clip-health.json", cs2GsiListener.GetHealthSnapshot());
                WriteJson(archive, "environment.json", new
                {
                    os = RuntimeInformation.OSDescription,
                    osVersion = Environment.OSVersion.VersionString,
                    architecture = RuntimeInformation.OSArchitecture.ToString(),
                    processorCount = Environment.ProcessorCount,
                    utc = DateTime.UtcNow
                });

                var skippedLogs = new List<string>();
                var logs = Directory.EnumerateFiles(logFolder, "clypdat*.log")
                    .Concat(new[] { Path.Combine(logFolder, "capture-worker.log") })
                    .Where(File.Exists);
                foreach (var log in logs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var contents = ReadLog(log);
                        var entry = archive.CreateEntry($"logs/{Path.GetFileName(log)}", CompressionLevel.Optimal);
                        using var output = new StreamWriter(entry.Open());
                        output.Write(Scrub(contents));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        skippedLogs.Add($"{Path.GetFileName(log)} ({error.GetType().Name})");
                    }
                }

                if (skippedLogs.Count > 0)
                {
                    var entry = archive.CreateEntry("bundle-warnings.txt", CompressionLevel.Optimal);
                    using var output = new StreamWriter(entry.Open());
                    output.WriteLine("The following logs could not be read while this bundle was created:");
                    foreach (var skippedLog in skippedLogs) output.WriteLine($"- {skippedLog}");
                }
            }

            File.Move(partialPath, path);
        }
        catch
        {
            try
            {
                if (File.Exists(partialPath)) File.Delete(partialPath);
            }
            catch
            {
                // Preserve the original export error if cleanup is blocked by another process.
            }

            throw;
        }

        AppLog.Info($"Capture diagnostic bundle created: {path}.");
        return path;
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteJson<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }

    // Diagnostics are meant to be handed to someone else, so scrubbing has to cover more
    // than the profile path. Logs carry the library and full-session folders (often on a
    // different drive entirely, so %USERPROFILE% never matched them), the machine name,
    // and UNC server names for network libraries.
    //
    // Longest-first ordering matters: replacing a short value that is a substring of a
    // longer path first would leave the longer one unscrubbed.
    private static string Scrub(string value)
    {
        var replacements = new List<(string Value, string Token)>();

        void Add(string? candidate, string token)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length > 2) replacements.Add((candidate, token));
        }

        try
        {
            var settings = ClypDat.Core.Settings.AppSettingsStore.Load();
            Add(settings.LibraryFolder, "%LIBRARY%");
            Add(settings.FullSessionRecordingFolder, "%FULLSESSION%");
        }
        catch
        {
            // Diagnostics must not fail because settings could not be read.
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        Add(Environment.MachineName, "%MACHINE%");
        Add(Environment.UserName, "%USERNAME%");

        foreach (var (candidate, token) in replacements.OrderByDescending(pair => pair.Value.Length))
        {
            value = value.Replace(candidate, token, StringComparison.OrdinalIgnoreCase);
        }

        // Any remaining UNC prefix names a server and share this machine can reach.
        value = System.Text.RegularExpressions.Regex.Replace(
            value, @"\\\\[^\\\s""']+\\[^\\\s""']+", "%UNC%");

        return value;
    }
}
