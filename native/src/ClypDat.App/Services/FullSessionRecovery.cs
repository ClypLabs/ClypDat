using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace ClypDat.App.Services;

// Only abandoned recordings take this path. Clean stop already writes indexes.
// Keep the original until a stream-copy remux has a readable duration and the
// same streams. Never remove recoverable footage when recovery fails.
internal static class FullSessionRecovery
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> Pending = new(StringComparer.OrdinalIgnoreCase);
    internal static string Marker(string path) => path + ".interrupted";
    internal static async Task<bool> RecoverAsync(string path, CancellationToken token = default)
    {
        if (!Pending.TryGetValue(path, out var pending))
        {
            if (!File.Exists(Marker(path)) || RecordingFileOwnership.IsActive(path)) return false;
            pending = Pending.GetOrAdd(path, key => new Lazy<Task<bool>>(() => RecoverCoreAsync(key)));
        }
        var task = pending.Value;
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        finally { if (task.IsCompleted) Pending.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(path, pending)); }
    }
    private static async Task<bool> RecoverCoreAsync(string path)
    {
        var folder = Path.Combine(Path.GetDirectoryName(path)!, ".clypdat-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var ownership = RecordingFileOwnership.Acquire(path);
            var original = await InspectAsync(path).ConfigureAwait(false);
            if (original.Streams.Length == 0) return false;
            Directory.CreateDirectory(folder);
            var output = Path.Combine(folder, "recovered" + Path.GetExtension(path));
            var args = new List<string> { "-v", "error", "-y", "-i", path, "-map", "0", "-c", "copy", "-map_metadata", "0" };
            MediaContainerOptions.AddFinalizedOptions(args, output);
            args.Add(output);
            await RunAsync(FfmpegPathResolver.FfmpegPath, args).ConfigureAwait(false);
            var recovered = await InspectAsync(output).ConfigureAwait(false);
            if (recovered.Duration <= 0 || !original.Streams.SequenceEqual(recovered.Streams)) return false;
            var created = File.GetCreationTimeUtc(path);
            File.SetCreationTimeUtc(output, created);
            File.Move(output, path, overwrite: true);
            File.SetCreationTimeUtc(path, created);
            File.Delete(Marker(path));
            AppLog.Info($"Recovered interrupted Full Session: {path}.");
            return true;
        }
        catch (Exception error) { AppLog.Error($"Full Session recovery deferred; original preserved: {path}.", error); return false; }
        finally { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { } }
    }
    private static async Task<(double Duration, string[] Streams)> InspectAsync(string path)
    {
        var result = await RunAsync(FfmpegPathResolver.FfprobePath, ["-v", "error", "-show_format", "-show_streams", "-of", "json", path]).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result);
        var duration = document.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var value)
            && double.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().Select(stream =>
            stream.GetProperty("codec_type").GetString() + ":" + stream.GetProperty("codec_name").GetString() + ":" +
            (stream.TryGetProperty("tags", out var tags) ? string.Join("|", tags.EnumerateObject()
                .Where(t => t.Name.Equals("title", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("handler_name", StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => t.Value.ToString())) : "")).ToArray();
        return (duration, streams);
    }
    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> args)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        // Remuxing a long interrupted recording can take minutes. This worker is
        // independent of capture and of a cancelled library refresh.
        await process.WaitForExitAsync().ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException(error);
        return await stdout.ConfigureAwait(false);
    }
}
