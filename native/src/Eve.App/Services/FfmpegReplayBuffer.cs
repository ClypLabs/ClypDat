using System.Diagnostics;
using System.Text;
using Eve.Capture.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Eve.App.Services;

public sealed class FfmpegReplayBuffer : IReplayBuffer, IDisposable
{
    private static readonly Lazy<HashSet<string>> SupportedInputFormats = new(LoadSupportedInputFormats);
    private static readonly Lazy<HashSet<string>> SupportedEncoders = new(LoadSupportedEncoders);
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly string _bufferFolder;
    private readonly string _logPath;
    private readonly string _pidPath;
    private Process? _process;
    private readonly List<AudioCaptureSession> _audioCaptures = new();
    private TimeSpan _duration = TimeSpan.FromSeconds(60);

    public FfmpegReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
        _bufferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "replay-buffer");
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "logs",
            "replay-buffer.log");
        _pidPath = Path.Combine(_bufferFolder, "ffmpeg.pid");
        CleanupStaleReplayProcess();
    }

    public bool IsRecording => _process is { HasExited: false };
    public TimeSpan Duration => _duration;
    public string LastError { get; private set; } = string.Empty;
    public event EventHandler? RecordingStopped;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording) await StopAsync(cancellationToken);
        CleanupStaleReplayProcess();

        var config = _configProvider();
        _duration = TimeSpan.FromSeconds(Math.Clamp(config.DurationSeconds, 30, 1200));
        Directory.CreateDirectory(_bufferFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        LastError = string.Empty;
        await File.WriteAllTextAsync(_logPath, $"EVE replay buffer {DateTime.Now:O}{Environment.NewLine}", cancellationToken);
        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "segment_*.mkv"))
        {
            TryDelete(file);
        }

        var args = BuildCaptureArguments(config);
        _process = StartCaptureProcess("ffmpeg", args);
        _process.Exited += Process_OnExited;
        await File.WriteAllTextAsync(_pidPath, _process.Id.ToString(), cancellationToken);
        StartAudioCaptures(config);

        var started = await WaitForFirstSegmentAsync(TimeSpan.FromSeconds(4), cancellationToken);
        if (started) return;

        if (_process.HasExited)
        {
            LastError = ReadTail(_logPath);
        }

        await StopAsync(cancellationToken);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(LastError)
            ? $"Replay buffer did not start. See {_logPath}."
            : LastError);
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
            StopAudioCaptures();
            process.Exited -= Process_OnExited;
            process.Dispose();
            TryDelete(_pidPath);
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
        var tempVideoPath = Path.Combine(_bufferFolder, $"replay_video_{Guid.NewGuid():N}.mkv");
        var outputPath = Path.Combine(outputFolder, $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mkv");
        await File.WriteAllLinesAsync(
            concatPath,
            files.Select(file => $"file '{EscapeConcatPath(file.FullName)}'"),
            new UTF8Encoding(false),
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
                tempVideoPath
            }, cancellationToken);

            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            await MuxAudioTracksAsync(tempVideoPath, outputPath, cancellationToken);
            return outputPath;
        }
        finally
        {
            TryDelete(concatPath);
            TryDelete(tempVideoPath);
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
            "-framerate", Math.Clamp(config.FrameRate, 15, 60).ToString(),
            "-offset_x", config.CaptureX.ToString(),
            "-offset_y", config.CaptureY.ToString(),
            "-video_size", $"{Math.Max(320, config.CaptureWidth)}x{Math.Max(240, config.CaptureHeight)}",
            "-i", "desktop"
        };

        var audioTitles = new List<string>();

        args.AddRange(new[] { "-map", "0:v:0" });
        var inputIndex = 1;
        var audioOutputIndex = 0;
        foreach (var title in audioTitles)
        {
            args.AddRange(new[] { "-map", $"{inputIndex}:a:0", $"-metadata:s:a:{audioOutputIndex}", $"title={title}" });
            inputIndex++;
            audioOutputIndex++;
        }

        args.AddRange(BuildVideoEncoderArguments(config.MaxHeight));
        if (audioTitles.Count > 0)
        {
            args.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", "192k"
            });
        }

        args.AddRange(new[]
        {
            "-f", "segment",
            "-segment_time", "2",
            "-segment_wrap", Math.Max(8, (int)Math.Ceiling(_duration.TotalSeconds / 2d) + 6).ToString(),
            "-reset_timestamps", "1",
            Path.Combine(_bufferFolder, "segment_%05d.mkv")
        });

        return args.ToArray();
    }

    private static string[] BuildVideoEncoderArguments(int maxHeight)
    {
        var height = Math.Clamp(maxHeight, 480, 1440);
        var width = Math.Min(3840, MakeEven((int)Math.Round(height * 16 / 9d)));
        var scale = $"scale=w={width}:h={height}:force_original_aspect_ratio=decrease:force_divisible_by=2";
        if (SupportsEncoder("h264_nvenc"))
        {
            return new[]
            {
                "-c:v", "h264_nvenc",
                "-vf", scale,
                "-preset", "p1",
                "-tune", "ull",
                "-rc", "vbr",
                "-cq", "23",
                "-b:v", "0",
                "-pix_fmt", "yuv420p"
            };
        }

        return new[]
        {
            "-c:v", "libx264",
            "-vf", scale,
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-threads", "4",
            "-pix_fmt", "yuv420p"
        };
    }

    private static void AddWasapiInput(List<string> args, string device)
    {
        args.AddRange(new[] { "-f", "wasapi", "-i", device });
    }

    private static void AddDshowAudioInput(List<string> args, string device)
    {
        args.AddRange(new[] { "-f", "dshow", "-i", $"audio={device}" });
    }

    private void StartAudioCaptures(ReplayBufferConfig config)
    {
        StopAudioCaptures();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            StartLoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), "Game Audio", "game");
            if (!string.IsNullOrWhiteSpace(config.ChatAudioDeviceId))
            {
                StartLoopbackCapture(enumerator.GetDevice(config.ChatAudioDeviceId), "Chat Audio", "chat");
            }

            if (!string.IsNullOrWhiteSpace(config.MicrophoneDeviceId))
            {
                StartMicrophoneCapture(enumerator.GetDevice(config.MicrophoneDeviceId), "Microphone", "microphone");
            }
        }
        catch (Exception error)
        {
            LastError = $"Audio capture unavailable: {error.Message}";
        }
    }

    private void StartLoopbackCapture(MMDevice device, string title, string fileName)
    {
        var path = Path.Combine(_bufferFolder, $"{fileName}.wav");
        TryDelete(path);
        var capture = new WasapiLoopbackCapture(device);
        _audioCaptures.Add(AudioCaptureSession.Start(capture, path, title));
    }

    private void StartMicrophoneCapture(MMDevice device, string title, string fileName)
    {
        var path = Path.Combine(_bufferFolder, $"{fileName}.wav");
        TryDelete(path);
        var capture = new WasapiCapture(device);
        _audioCaptures.Add(AudioCaptureSession.Start(capture, path, title));
    }

    private void StopAudioCaptures()
    {
        foreach (var capture in _audioCaptures.ToArray())
        {
            capture.Dispose();
        }

        _audioCaptures.Clear();
    }

    private async Task MuxAudioTracksAsync(string videoPath, string outputPath, CancellationToken cancellationToken)
    {
        var audioFiles = new[] { "game.wav", "chat.wav", "microphone.wav" }
            .Select(path => Path.Combine(_bufferFolder, path))
            .Where(path => File.Exists(path) && new FileInfo(path).Length > 44)
            .ToArray();
        if (audioFiles.Length == 0)
        {
            File.Copy(videoPath, outputPath, overwrite: true);
            return;
        }

        var args = new List<string> { "-y", "-i", videoPath };
        foreach (var audioFile in audioFiles)
        {
            args.AddRange(new[] { "-sseof", $"-{Math.Max(1, _duration.TotalSeconds):0.###}", "-i", audioFile });
        }

        args.AddRange(new[] { "-map", "0:v:0", "-c:v", "copy" });
        for (var i = 0; i < audioFiles.Length; i++)
        {
            args.AddRange(new[] { "-map", $"{i + 1}:a:0", $"-metadata:s:a:{i}", $"title={AudioTitleForPath(audioFiles[i])}" });
        }

        args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-shortest", outputPath });
        var result = await RunProcessAsync("ffmpeg", args, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
    }

    private static string AudioTitleForPath(string path)
    {
        return Path.GetFileNameWithoutExtension(path).ToLowerInvariant() switch
        {
            "chat" => "Chat Audio",
            "microphone" => "Microphone",
            _ => "Game Audio"
        };
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

    private Process StartCaptureProcess(string fileName, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            try
            {
                File.AppendAllText(_logPath, e.Data + Environment.NewLine);
            }
            catch
            {
                // Logging must not kill capture.
            }
        };
        process.BeginErrorReadLine();
        return process;
    }

    private async Task<bool> WaitForFirstSegmentAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited) return false;
            if (Directory.EnumerateFiles(_bufferFolder, "segment_*.mkv").Any(path => new FileInfo(path).Length > 0))
            {
                return true;
            }

            await Task.Delay(150, cancellationToken);
        }

        return _process is { HasExited: false };
    }

    private void Process_OnExited(object? sender, EventArgs e)
    {
        LastError = ReadTail(_logPath);
        TryDelete(_pidPath);
        RecordingStopped?.Invoke(this, EventArgs.Empty);
    }

    private static string ReadTail(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            var lines = File.ReadLines(path).TakeLast(20);
            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        using var process = StartProcess(fileName, args, redirect: true);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask, await errorTask);
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

    private void CleanupStaleReplayProcess()
    {
        try
        {
            if (!File.Exists(_pidPath)) return;
            var text = File.ReadAllText(_pidPath).Trim();
            if (int.TryParse(text, out var pid))
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited && string.Equals(process.ProcessName, "ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // Stale cleanup is best effort and must not block app launch.
        }
        finally
        {
            TryDelete(_pidPath);
        }
    }

    private static bool SupportsInputFormat(string name)
    {
        return SupportedInputFormats.Value.Contains(name);
    }

    private static bool SupportsEncoder(string name)
    {
        return SupportedEncoders.Value.Contains(name);
    }

    private static HashSet<string> LoadSupportedInputFormats()
    {
        var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = RunProcessAsync("ffmpeg", new[] { "-hide_banner", "-formats" }, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var text = result.Error + Environment.NewLine + result.Output;
            foreach (var line in text.Split(Environment.NewLine))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && parts[0].Contains('D'))
                {
                    formats.Add(parts[1]);
                }
            }
        }
        catch
        {
            // Missing ffmpeg support is reported when capture starts.
        }

        return formats;
    }

    private static HashSet<string> LoadSupportedEncoders()
    {
        var encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = RunProcessAsync("ffmpeg", new[] { "-hide_banner", "-encoders" }, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var text = result.Error + Environment.NewLine + result.Output;
            foreach (var line in text.Split(Environment.NewLine))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && parts[0].Contains('V'))
                {
                    encoders.Add(parts[1]);
                }
            }
        }
        catch
        {
            // Missing ffmpeg support is reported when capture starts.
        }

        return encoders;
    }

    private static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "\\\\").Replace("'", "'\\''");
    }

    private static int MakeEven(int value)
    {
        return value % 2 == 0 ? value : value - 1;
    }
}

public sealed record ReplayBufferConfig(
    int DurationSeconds,
    int MaxHeight,
    int FrameRate,
    int CaptureX,
    int CaptureY,
    int CaptureWidth,
    int CaptureHeight,
    string ChatAudioDeviceName,
    string ChatAudioDeviceId,
    string MicrophoneDeviceId,
    string MicrophoneDeviceName);

internal sealed class AudioCaptureSession : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly FileStream _stream;
    private readonly WaveFileWriter _writer;
    private readonly object _lock = new();

    private AudioCaptureSession(IWaveIn capture, FileStream stream, WaveFileWriter writer, string title)
    {
        _capture = capture;
        _stream = stream;
        _writer = writer;
        Title = title;
    }

    public string Title { get; }

    public static AudioCaptureSession Start(IWaveIn capture, string path, string title)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var writer = new WaveFileWriter(stream, capture.WaveFormat);
        var session = new AudioCaptureSession(capture, stream, writer, title);
        capture.DataAvailable += session.Capture_OnDataAvailable;
        capture.StartRecording();
        return session;
    }

    public void Dispose()
    {
        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // Stop is best effort.
        }

        _capture.DataAvailable -= Capture_OnDataAvailable;
        lock (_lock)
        {
            _writer.Dispose();
            _stream.Dispose();
        }

        _capture.Dispose();
    }

    private void Capture_OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _writer.Flush();
        }
    }
}
