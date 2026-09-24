using System.IO.Compression;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

public static class CaptureDiagnosticBundle
{
    public static string Create(IReplayBuffer? replayBuffer, Cs2GsiListener? cs2GsiListener = null)
        => Create(replayBuffer, cs2GsiListener, AppLog.LogFolder, DateTime.Now);

    public static string CreateForUpload(IReplayBuffer? replayBuffer, Cs2GsiListener? cs2GsiListener = null)
        => Create(replayBuffer, cs2GsiListener, AppLog.LogFolder, DateTime.Now, recentOnly: true);

    internal static string Create(
        IReplayBuffer? replayBuffer,
        Cs2GsiListener? cs2GsiListener,
        string logFolder,
        DateTime now,
        bool recentOnly = false)
    {
        Directory.CreateDirectory(logFolder);
        var stopwatch = Stopwatch.StartNew();
        var path = Path.Combine(logFolder, $"clypdat-capture-diagnostics-{now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.zip");
        var partialPath = $"{path}.partial";
        var skippedLogs = new List<string>();
        var redactions = GetRedactions();
        var appLogs = Directory.EnumerateFiles(logFolder, "clypdat*.log");
        if (recentOnly) appLogs = appLogs.OrderByDescending(File.GetLastWriteTimeUtc).Take(4);
        var logs = appLogs
            .Concat(new[] { Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "capture-worker.log") })
            .Where(File.Exists);
        var snapshots = new List<LogSnapshot>();
        foreach (var log in logs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { snapshots.Add(new LogSnapshot(log, new FileInfo(log).Length)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { skippedLogs.Add($"{Path.GetFileName(log)} ({error.GetType().Name})"); }
        }
        var sourceBytes = snapshots.Sum(snapshot => Math.Min(snapshot.Length, recentOnly ? 512 * 1024L : long.MaxValue));

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
                    appVersion = AppUpdateService.CurrentVersion.ToString(),
                    os = RuntimeInformation.OSDescription,
                    osVersion = Environment.OSVersion.VersionString,
                    architecture = RuntimeInformation.OSArchitecture.ToString(),
                    processorCount = Environment.ProcessorCount,
                    utc = DateTime.UtcNow
                });

                if (recentOnly) WriteJson(archive, "bundle-scope.json", new
                {
                    recentLogsOnly = true, maximumAppLogs = 4, maximumBytesPerLog = 512 * 1024
                });

                foreach (var snapshot in snapshots)
                {
                    try
                    {
                        var maximumBytes = recentOnly ? 512 * 1024L : long.MaxValue;
                        var start = Math.Max(0, snapshot.Length - maximumBytes);
                        var readLength = snapshot.Length - start;
                        var entry = archive.CreateEntry($"logs/{Path.GetFileName(snapshot.Path)}",
                            recentOnly ? CompressionLevel.Optimal : CompressionLevel.Fastest);
                        using var output = new StreamWriter(entry.Open());
                        StreamLog(snapshot, start, readLength, output, redactions);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        skippedLogs.Add($"{Path.GetFileName(snapshot.Path)} ({error.GetType().Name})");
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

        stopwatch.Stop();
        var archiveBytes = new FileInfo(path).Length;
        AppLog.Info($"Capture diagnostic bundle created: {path}; sourceBytes={sourceBytes} archiveBytes={archiveBytes} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.");
        return path;
    }

    private sealed record LogSnapshot(string Path, long Length);

    private static void StreamLog(LogSnapshot snapshot, long start, long length, TextWriter output,
        IReadOnlyList<(string Value, string Token)> redactions)
    {
        using var file = new FileStream(snapshot.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        file.Seek(start, SeekOrigin.Begin);
        using var limited = new SnapshotReadStream(file, length);
        using var reader = new StreamReader(limited);
        if (start > 0)
        {
            int skipped;
            while ((skipped = reader.Read()) >= 0 && skipped is not ('\r' or '\n')) { }
            if (skipped == '\r' && reader.Peek() == '\n') _ = reader.Read();
            output.WriteLine("[Earlier log content omitted from upload]");
        }
        var line = new StringBuilder();
        int character;
        while ((character = reader.Read()) >= 0)
        {
            if (character is '\r' or '\n')
            {
                output.Write(Scrub(line.ToString(), redactions));
                output.Write((char)character);
                if (character == '\r' && reader.Peek() == '\n') output.Write((char)reader.Read());
                line.Clear();
            }
            else line.Append((char)character);
        }
        if (line.Length > 0) output.Write(Scrub(line.ToString(), redactions));
    }

    private sealed class SnapshotReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _remaining;
        public SnapshotReadStream(Stream inner, long length) { _inner = inner; _length = length; _remaining = length; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, _remaining);
            if (count <= 0) return 0;
            var read = _inner.Read(buffer, offset, count);
            _remaining -= read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, _remaining);
            if (count <= 0) return 0;
            var read = _inner.Read(buffer[..count]);
            _remaining -= read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private static IReadOnlyList<(string Value, string Token)> GetRedactions()
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
        catch { }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        Add(Environment.MachineName, "%MACHINE%");
        Add(Environment.UserName, "%USERNAME%");
        return replacements.OrderByDescending(pair => pair.Value.Length).ToArray();
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
    private static string Scrub(string value, IReadOnlyList<(string Value, string Token)> replacements)
    {
        foreach (var (candidate, token) in replacements)
        {
            value = value.Replace(candidate, token, StringComparison.OrdinalIgnoreCase);
        }

        // Any remaining UNC prefix names a server and share this machine can reach.
        value = System.Text.RegularExpressions.Regex.Replace(
            value, @"\\\\[^\\\s""']+\\[^\\\s""']+", "%UNC%");

        value = System.Text.RegularExpressions.Regex.Replace(value,
            @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+", "Bearer [REDACTED]");
        value = System.Text.RegularExpressions.Regex.Replace(value,
            @"(?i)([""']?(?:access_token|refresh_token|client_secret|authorization)[""']?\s*[:=]\s*[""']?)[^\s""',}]+", "$1[REDACTED]");

        return value;
    }
}
