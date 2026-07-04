using System.Diagnostics;
using System.Text;
using Eve.Capture.Abstractions;

namespace Eve.App.Services;

public sealed class FfmpegReplayBuffer : IReplayBuffer, IDisposable
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly string _bufferFolder;
    private Process? _process;
    private TimeSpan _duration = TimeSpan.FromSeconds(60);

    public FfmpegReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
        _bufferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "replay-buffer");
    }

    public bool IsRecording => _process is { HasExited: false };
    public TimeSpan Duration => _duration;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording) return Task.CompletedTask;

        var config = _configProvider();
        _duration = TimeSpan.FromSeconds(Math.Clamp(config.DurationSeconds, 10, 600));
        Directory.CreateDirectory(_bufferFolder);
        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "segment_*.mkv"))
        {
            TryDelete(file);
        }

        var args = BuildCaptureArguments(config);
        _process = StartProcess("ffmpeg", args, redirect: false);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        catch
        {
            // Replay stop must never block app shutdown.
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

        var cutoff = DateTime.UtcNow - TimeSpan.FromMilliseconds(800);
        var files = Directory.EnumerateFiles(_bufferFolder, "segment_*.mkv")
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.Length > 0 && file.LastWriteTimeUtc < cutoff)
            .OrderBy(file => file.LastWriteTimeUtc)
            .TakeLast(Math.Max(2, (int)Math.Ceiling(_duration.TotalSeconds / 2) + 4))
            .ToArray();

        if (files.Length == 0) throw new InvalidOperationException("Replay buffer has no finished segments yet.");

        var concatPath = Path.Combine(_bufferFolder, $"concat_{Guid.NewGuid():N}.txt");
        var outputPath = Path.Combine(outputFolder, $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mkv");
        await File.WriteAllLinesAsync(
            concatPath,
            files.Select(file => $"file '{file.FullName.Replace("'", "'\\''")}'"),
            Encoding.UTF8,
            cancellationToken);

        try
        {
            var result = await RunProcessAsync("ffmpeg", new[]
            {
                "-y",
                "-f", "concat",
                "-safe", "0",
                "-i", concatPath,
                "-c", "copy",
                outputPath
            }, cancellationToken);

            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            return outputPath;
        }
        finally
        {
            TryDelete(concatPath);
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
    }

    private string[] BuildCaptureArguments(ReplayBufferConfig config)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-f", "gdigrab",
            "-framerate", "60",
            "-i", "desktop"
        };

        AddWasapiInput(args, "default");
        if (!string.IsNullOrWhiteSpace(config.ChatAudioDeviceName)) AddWasapiInput(args, config.ChatAudioDeviceName);
        if (!string.IsNullOrWhiteSpace(config.MicrophoneDeviceName)) AddWasapiInput(args, config.MicrophoneDeviceName);

        args.AddRange(new[] { "-map", "0:v:0" });
        var inputIndex = 1;
        var audioOutputIndex = 0;
        foreach (var title in BuildAudioTitles(config))
        {
            args.AddRange(new[] { "-map", $"{inputIndex}:a:0", $"-metadata:s:a:{audioOutputIndex}", $"title={title}" });
            inputIndex++;
            audioOutputIndex++;
        }

        args.AddRange(new[]
        {
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "192k",
            "-f", "segment",
            "-segment_time", "2",
            "-segment_wrap", Math.Max(8, (int)Math.Ceiling(config.DurationSeconds / 2d) + 6).ToString(),
            "-reset_timestamps", "1",
            Path.Combine(_bufferFolder, "segment_%05d.mkv")
        });

        return args.ToArray();
    }

    private static IEnumerable<string> BuildAudioTitles(ReplayBufferConfig config)
    {
        yield return "Game Audio";
        if (!string.IsNullOrWhiteSpace(config.ChatAudioDeviceName)) yield return "Chat Audio";
        if (!string.IsNullOrWhiteSpace(config.MicrophoneDeviceName)) yield return "Microphone";
    }

    private static void AddWasapiInput(List<string> args, string device)
    {
        args.AddRange(new[] { "-f", "wasapi", "-i", device });
    }

    private static Process StartProcess(string fileName, IEnumerable<string> args, bool redirect)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = redirect,
            RedirectStandardOutput = redirect
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
    }

    private static async Task<(int ExitCode, string Error)> RunProcessAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        using var process = StartProcess(fileName, args, redirect: true);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await errorTask);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Segment cleanup is best effort.
        }
    }
}

public sealed record ReplayBufferConfig(
    int DurationSeconds,
    string ChatAudioDeviceName,
    string MicrophoneDeviceName);
