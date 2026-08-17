using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ClypDat.Capture.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClypDat.App.Services;

public sealed class FfmpegReplayBuffer : IReplayBuffer, IDisposable
{
    private static readonly Lazy<HashSet<string>> SupportedInputFormats = new(LoadSupportedInputFormats);
    private static readonly Lazy<HashSet<string>> SupportedEncoders = new(LoadSupportedEncoders);
    private static readonly Lazy<HashSet<string>> SupportedFilters = new(LoadSupportedFilters);
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly string _bufferFolder;
    private readonly string _logPath;
    private readonly string _pidPath;
    private Process? _process;
    private readonly List<AudioCaptureSession> _audioCaptures = new();
    private CancellationTokenSource? _cleanupCts;
    private Task? _cleanupTask;
    private ReplayBufferConfig? _lastConfig;
    private TimeSpan _duration = TimeSpan.FromSeconds(60);

    public FfmpegReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
        _bufferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClypDat",
            "replay-buffer");
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClypDat",
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
        _lastConfig = config;
        _duration = TimeSpan.FromSeconds(Math.Clamp(config.DurationSeconds, 30, 1200));
        Directory.CreateDirectory(_bufferFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        LastError = string.Empty;
        await File.WriteAllTextAsync(_logPath, $"ClypDat replay buffer {DateTime.Now:O}{Environment.NewLine}", cancellationToken);
        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "segment_*.mkv"))
        {
            TryDelete(file);
        }

        var attempts = SupportsFilter("ddagrab")
            ? new[] { CaptureBackend.Ddagrab, CaptureBackend.Gdigrab }
            : new[] { CaptureBackend.Gdigrab };
        foreach (var backend in attempts)
        {
            var args = BuildCaptureArguments(config, backend);
            _process = StartCaptureProcess("ffmpeg", args);
            _process.Exited += Process_OnExited;
            await File.WriteAllTextAsync(_pidPath, _process.Id.ToString(), cancellationToken);

            var started = await WaitForFirstSegmentAsync(TimeSpan.FromSeconds(4), cancellationToken);
            if (started)
            {
                StartAudioCaptures(config);
                StartCleanupLoop(cancellationToken);
                return;
            }

            if (_process.HasExited)
            {
                LastError = ReadTail(_logPath);
            }

            await StopVideoProcessAsync(cancellationToken);
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(LastError)
            ? $"Replay buffer did not start. See {_logPath}."
            : LastError);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        _process = null;
        StopCleanupLoop();
        if (process is null)
        {
            StopAudioCaptures();
            return;
        }

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

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null)
    {
        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

        var requestedDuration = clipWindow is null
            ? _duration.TotalSeconds
            : Math.Max(1, (clipWindow.EndUtc - clipWindow.StartUtc).TotalSeconds);

        PruneOldSegments();
        var files = GetFinishedVideoSegments()
            .TakeLast(Math.Max(2, (int)Math.Ceiling(requestedDuration / 2) + 4))
            .ToArray();

        if (files.Length == 0) throw new InvalidOperationException("Replay buffer has no finished segments yet.");

        var config = _lastConfig;
        StopAudioCaptures();
        var concatPath = Path.Combine(_bufferFolder, $"concat_{Guid.NewGuid():N}.txt");
        var tempVideoPath = Path.Combine(_bufferFolder, $"replay_video_{Guid.NewGuid():N}.mkv");
        var clipName = string.IsNullOrWhiteSpace(titleOverride) ? config?.GameDisplayName ?? string.Empty : titleOverride;
        var gameFolder = Path.Combine(outputFolder, ClipFileNaming.BuildBaseName(config?.GameDisplayName ?? string.Empty));
        Directory.CreateDirectory(gameFolder);
        var outputPath = ClipFileNaming.BuildUniquePath(gameFolder, ClipFileNaming.BuildFileName(clipName, DateTime.Now, "mkv", config?.ClipFileNameScheme ?? ClipFileNaming.StandardScheme, config?.CustomClipFileNameTemplate ?? string.Empty, config?.GameDisplayName));
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
            await MuxAudioTracksAsync(tempVideoPath, outputPath, cancellationToken, requestedDuration);
            return await ClipMetadataTagger.TagCaptureBackendAsync(outputPath, "FFmpeg", cancellationToken);
        }
        finally
        {
            TryDelete(concatPath);
            TryDelete(tempVideoPath);
            if (IsRecording && config is not null)
            {
                StartAudioCaptures(config);
            }
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
    }

    private string[] BuildCaptureArguments(ReplayBufferConfig config, CaptureBackend backend)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin",
            "-rtbufsize", "128M"
        };

        var frameRate = Math.Clamp(config.FrameRate, 30, 144).ToString();
        var size = $"{Math.Max(320, config.CaptureWidth)}x{Math.Max(240, config.CaptureHeight)}";
        if (backend == CaptureBackend.Ddagrab)
        {
            args.AddRange(new[]
            {
                "-f", "lavfi",
                "-i", $"ddagrab=framerate={frameRate}:video_size={size}:offset_x={config.CaptureX}:offset_y={config.CaptureY}:output_fmt=8bit:allow_fallback=1"
            });
        }
        else
        {
            args.AddRange(new[]
            {
                "-f", "gdigrab",
                "-framerate", frameRate,
                "-offset_x", config.CaptureX.ToString(),
                "-offset_y", config.CaptureY.ToString(),
                "-video_size", size,
                "-i", "desktop"
            });
        }

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

        args.AddRange(BuildVideoEncoderArguments(config));
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
            "-reset_timestamps", "1",
            Path.Combine(_bufferFolder, "segment_%05d.mkv")
        });

        return args.ToArray();
    }

    private static string[] BuildVideoEncoderArguments(ReplayBufferConfig config)
    {
        var height = Math.Clamp(config.MaxHeight, 480, 1440);
        var width = Math.Min(3840, MakeEven((int)Math.Round(height * 16 / 9d)));
        var scale = $"scale=w={width}:h={height}:force_original_aspect_ratio=decrease:force_divisible_by=2";
        var bitrate = Math.Clamp(config.BitrateMbps, 5, 100) * 1_000_000;
        var rate = new[] { "-b:v", bitrate.ToString(), "-maxrate", bitrate.ToString(), "-bufsize", bitrate.ToString() };
        // NVENC -> AMD AMF -> Intel QSV -> CPU, the same ladder the native
        // capture engine walks (NativeReplayBuffer.EncoderCandidates). Only
        // NVENC was checked before, so an AMD or Intel machine skipped straight
        // to libx264 and recorded on the CPU with a hardware encoder sitting
        // idle. Each vendor gets its own low-latency settings; the flag names
        // do not carry across (NVENC's -preset/-tune/-cq have no meaning to
        // AMF or QSV), which is why this is a ladder and not one arg list.
        if (SupportsEncoder("h264_nvenc"))
        {
            return new[]
            {
                "-c:v", "h264_nvenc",
                "-vf", scale,
                "-preset", "p1",
                "-tune", "ull",
                "-rc", "cbr",
                "-pix_fmt", "yuv420p"
            }.Concat(rate).ToArray();
        }

        if (SupportsEncoder("h264_amf"))
        {
            return new[]
            {
                "-c:v", "h264_amf",
                "-vf", scale,
                "-usage", "ultralowlatency",
                "-quality", "speed",
                "-pix_fmt", "yuv420p"
            }.Concat(new[] { "-rc", "cbr" }).Concat(rate).ToArray();
        }

        if (SupportsEncoder("h264_qsv"))
        {
            return new[]
            {
                "-c:v", "h264_qsv",
                "-vf", scale,
                "-preset", "veryfast",
                "-pix_fmt", "nv12"
            }.Concat(rate).ToArray();
        }

        return new[]
        {
            "-c:v", "libx264",
            "-vf", scale,
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-threads", "2",
            "-pix_fmt", "yuv420p"
        }.Concat(rate).ToArray();
    }

    private static void AddWasapiInput(List<string> args, string device)
    {
        args.AddRange(new[] { "-f", "wasapi", "-i", device });
    }

    private static void AddDshowAudioInput(List<string> args, string device)
    {
        args.AddRange(new[] { "-f", "dshow", "-i", $"audio={device}" });
    }

    private async Task StopVideoProcessAsync(CancellationToken cancellationToken)
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
            // Fallback startup cleanup is best effort.
        }
        finally
        {
            process.Exited -= Process_OnExited;
            process.Dispose();
            TryDelete(_pidPath);
        }
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

            var micDevice = ResolveMicrophoneDevice(enumerator, config.MicrophoneDeviceIds.FirstOrDefault() ?? string.Empty);
            if (micDevice is not null)
            {
                StartMicrophoneCapture(micDevice, "Microphone", "microphone");
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

    private static MMDevice? ResolveMicrophoneDevice(MMDeviceEnumerator enumerator, string microphoneDeviceId)
    {
        if (string.IsNullOrWhiteSpace(microphoneDeviceId) || microphoneDeviceId == AudioDeviceOption.DefaultDeviceId)
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch
            {
                return null;
            }
        }

        return enumerator.GetDevice(microphoneDeviceId);
    }

    private void StopAudioCaptures()
    {
        foreach (var capture in _audioCaptures.ToArray())
        {
            capture.Dispose();
        }

        _audioCaptures.Clear();
    }

    private void StartCleanupLoop(CancellationToken cancellationToken)
    {
        StopCleanupLoop();
        _cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cleanupCts.Token;
        _cleanupTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                PruneOldSegments();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private void StopCleanupLoop()
    {
        try
        {
            _cleanupCts?.Cancel();
        }
        catch
        {
            // Cleanup stop is best effort.
        }
        finally
        {
            _cleanupCts?.Dispose();
            _cleanupCts = null;
            _cleanupTask = null;
        }
    }

    private void PruneOldSegments()
    {
        var cutoff = DateTime.UtcNow - _duration - TimeSpan.FromSeconds(12);
        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "segment_*.mkv"))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists && info.LastWriteTimeUtc < cutoff)
                {
                    TryDelete(file);
                }
            }
            catch
            {
                // Prune is best effort.
            }
        }
    }

    private async Task MuxAudioTracksAsync(string videoPath, string outputPath, CancellationToken cancellationToken, double clipDurationSeconds)
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
            args.AddRange(new[] { "-sseof", $"-{Math.Max(1, clipDurationSeconds):0.###}", "-i", audioFile });
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
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Priority is best effort.
        }

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

    private IReadOnlyList<FileInfo> GetFinishedVideoSegments()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMilliseconds(800);
        var segments = Directory.EnumerateFiles(_bufferFolder, "segment_*.mkv")
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.Length > 0 && file.LastWriteTimeUtc < cutoff)
            .Select(file => new { File = file, Index = SegmentIndex(file.Name) })
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .ToArray();

        if (segments.Length <= 1) return Array.Empty<FileInfo>();

        return segments
            .Take(segments.Length - 1)
            .Select(item => item.File)
            .ToArray();
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

    private static bool SupportsFilter(string name)
    {
        return SupportedFilters.Value.Contains(name);
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

    private static HashSet<string> LoadSupportedFilters()
    {
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = RunProcessAsync("ffmpeg", new[] { "-hide_banner", "-filters" }, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var text = result.Error + Environment.NewLine + result.Output;
            foreach (var line in text.Split(Environment.NewLine))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    filters.Add(parts[1]);
                }
            }
        }
        catch
        {
            // Missing filter support falls back to gdigrab.
        }

        return filters;
    }

    private static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "\\\\").Replace("'", "'\\''");
    }

    private static int MakeEven(int value)
    {
        return value % 2 == 0 ? value : value - 1;
    }

    private static int SegmentIndex(string fileName)
    {
        var match = Regex.Match(fileName, @"segment_(\d+)\.mkv$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : -1;
    }

    private enum CaptureBackend
    {
        Ddagrab,
        Gdigrab
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
    IReadOnlyList<string> ChatAudioProcessNames,
    IReadOnlyList<string> MicrophoneDeviceIds,
    string MicrophoneDeviceName,
    IReadOnlyList<string> GameAudioExcludedProcesses,
    string GameDisplayName,
    string GameExecutableName,
    string GameWindowTitle,
    string GameWindowClass,
    string Backend = "Auto",
    long GameWindowHandle = 0,
    bool FullSessionRecordingEnabled = false,
    string FullSessionRecordingFolder = "",
    string FullSessionVideoCodec = "H.264",
    int FullSessionQuotaGb = 0,
    bool FullSessionBackgroundFinalize = true,
    string ClipFileNameScheme = "Standard",
    string CustomClipFileNameTemplate = "{datetime:yyyy-MM-dd HH-mm-ss} - {title}",
    string LibraryFolder = "",
    // Native engine encoder controls - see AppSettings for what each means.
    string VideoCodec = "H.264",
    string EncoderMode = "GPU",
    string EncoderPreset = "P1",
    int BitrateMbps = 15,
    string CaptureSource = "Game",
    string CaptureMonitorDeviceName = "",
    bool CaptureCursor = false,
    string ProcessPriority = "Normal",
    string SaveReplayHotkey = "Ctrl+Shift+F9");

internal sealed class AudioCaptureSession : IDisposable
{
    private readonly IWaveIn _capture;
    // FileStream for a disk-backed capture (Full Session - needs to survive
    // for the whole, potentially hours-long, recording, so it can't live in
    // RAM), MemoryStream for a RAM-backed one (plain replay buffer - see
    // StartInMemory). Everything below this field (gap-fill, peak
    // diagnostics, placement logging) already only ever touches _writer, not
    // _stream directly, so none of that needed to change either way - only
    // SnapshotTo/Dispose/TrimTo do.
    //
    // NOT readonly - TrimTo (RAM-backed only) periodically swaps both for a
    // freshly-compacted MemoryStream+WaveFileWriter holding just the
    // still-retained tail of audio, discarding everything older. Always
    // reassigned together, always under _lock.
    private Stream _stream;
    private WaveFileWriter _writer;
    private readonly object _lock = new();
    private bool _firstSampleSeen;

    private AudioCaptureSession(IWaveIn capture, Stream stream, WaveFileWriter writer, string title)
    {
        _capture = capture;
        _stream = stream;
        _writer = writer;
        Title = title;
    }

    public string Title { get; }

    // StartRecording() returns as soon as the request is issued, not once audio is
    // actually flowing - WASAPI endpoint (loopback) capture has noticeably more
    // startup latency than per-process loopback or mic capture, so stamping
    // "started" at the StartRecording() call site under-estimates Game Audio's
    // true start more than it does Chat/Microphone. This records when the first
    // real sample actually arrived, which every capture kind can be aligned against
    // on equal footing.
    public DateTime? FirstSampleUtc { get; private set; }

    // Set when the underlying capture stopped with an error (device loss, a
    // throw escaping the write path). The pipeline reaps Died captures on its
    // route timer and starts fresh ones for the same source.
    public bool Died { get; internal set; }

    // Data bytes written so far (audio + backfilled silence). Exact, unlike
    // FileInfo.Length on a file with an open write handle, whose directory
    // entry Windows only updates lazily.
    public long BytesWritten { get { lock (_lock) return _bytesWritten; } }

    public int AverageBytesPerSecond => _capture.WaveFormat.AverageBytesPerSecond;

    // True for a capture started via StartInMemory - AudioCapturePipeline's
    // roll-check uses this to apply the much smaller "replay window + slack"
    // duration cap instead of the 4GiB RIFF cap that only a disk-backed,
    // unbounded-duration Full Session capture needs to worry about.
    public bool IsMemoryBacked => _stream is MemoryStream;

    public static AudioCaptureSession Start(IWaveIn capture, string path, string title)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        return StartOn(capture, stream, title);
    }

    // RAM-backed capture - used for the plain replay-buffer window (not Full
    // Session, which needs disk: a recording that can run for hours can't
    // reasonably live entirely in memory). SnapshotTo below still produces a
    // real WAV file on demand from whatever's currently buffered, so nothing
    // downstream (AudioCapturePipeline's ffmpeg-based windowing/mixing/
    // alignment) needs to know or care which mode a given capture is in.
    //
    // capacityHintBytes pre-sizes the MemoryStream to roughly the expected
    // peak (AudioCapturePipeline passes its roll threshold - the same size
    // it'll actually roll this capture at) - MemoryStream's default no-
    // capacity constructor grows by doubling whenever it runs out of room,
    // which can transiently hold up to ~2x the final data size in memory
    // right before a growth (allocate new double-size array, copy old
    // contents in, THEN release the old one) and repeatedly reallocates/
    // copies along the way. Pre-sizing avoids both: one allocation, no
    // doubling overshoot, no copy-on-grow churn during 100Hz writes.
    public static AudioCaptureSession StartInMemory(IWaveIn capture, string title, int capacityHintBytes = 0)
    {
        var stream = capacityHintBytes > 0 ? new MemoryStream(capacityHintBytes) : new MemoryStream();
        return StartOn(capture, stream, title);
    }

    private static AudioCaptureSession StartOn(IWaveIn capture, Stream stream, string title)
    {
        var writer = new WaveFileWriter(stream, capture.WaveFormat);
        var session = new AudioCaptureSession(capture, stream, writer, title);
        capture.DataAvailable += session.Capture_OnDataAvailable;
        // A capture that dies mid-session (device error, a throw escaping the
        // write path) previously vanished without a trace - the capture thread
        // swallowed the error and nothing was listening here. The WAV then
        // just stopped growing and saved clips lost that track with no log to
        // explain why. Died flags it so the pipeline's route timer can reap
        // and restart it instead of treating it as live forever.
        capture.RecordingStopped += (_, stopped) =>
        {
            if (stopped.Exception is not null)
            {
                session.Died = true;
                AppLog.Error($"Audio capture stopped unexpectedly: {title}", stopped.Exception);
            }
        };
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

    // lastSampleUtc reports the timeline moment the snapshot's final byte
    // corresponds to. It MUST be stamped here, before the copy: copying a
    // multi-GB session WAV takes 1-2s, and end-anchoring against a "now"
    // taken after the copy shifted every track's anchor late by its own copy
    // duration - game audio lagged ~2s in clips saved late in long sessions.
    // earliestNeededUtc trims the copy to just the tail the caller will
    // actually read. Captures are disk-backed and unbounded now (RAM-backed
    // was reverted, so TrimTo never runs - see AudioCapturePipeline's
    // StartSession), which means the session WAV grows until the 4GiB roll.
    // Copying all of it to produce a 60s clip meant several GB of read+write
    // per save, per track, and that disk storm is what froze the game and the
    // mouse mid-match. Only the audio from the requested window onward is ever
    // used - SnapshotAudioFileAsync immediately -ss seeks past everything
    // before it - so the rest is pure waste. Pass null to copy the whole
    // capture, which is what a Full Session finalize legitimately needs.
    //
    // Only the bookkeeping runs under _lock. The bulk copy deliberately does
    // NOT: it used to, and _lock is the same lock Capture_OnDataAvailable
    // takes to accept a WASAPI packet, so every save froze all three capture
    // callbacks for as long as its copy took. A real session shows the whole
    // chain in one place - the mic's tail copy finished at 22:14:22.290 and
    // the very next line is "Audio capture gap placed: Microphone,
    // gap=6670ms". Every save punched a hole in every track the exact size of
    // its own copy, silently, and the tracks then had to be silence-padded
    // over it. The bytes being copied are already immutable (the writer only
    // ever appends past _bytesWritten) and the source is re-opened on its own
    // read handle, so holding the lock bought nothing.
    public bool SnapshotTo(string path, DateTime? earliestNeededUtc, out DateTime lastSampleUtc)
    {
        string? sourceFileName = null;
        long copyFromOffset = 0;
        long copyBytes = 0;

        lock (_lock)
        {
            lastSampleUtc = MonotonicClock.UtcNow;
            try
            {
                // Pad any in-progress delivery gap up to "now" first, so the
                // snapshot's last byte genuinely corresponds to the snapshot
                // moment - the end-anchored alignment in AudioCapturePipeline
                // depends on that. MonotonicClock, not DateTime: a system
                // clock step mid-session made this pad silently stop firing
                // (the stepped-back "now" implied fewer bytes than written),
                // desyncing every track by its own flush-phase amount.
                WriteSilenceForDeliveryGapLocked(lastSampleUtc, minGapMs: 30);
                _writer.Flush();

                // Keeping the TAIL is what makes this safe for alignment.
                // lastSampleUtc still describes the final byte, and the
                // pipeline derives the snapshot's start as
                // lastSampleUtc - reader.TotalTime, so a shorter file simply
                // reports a later start - by exactly the amount trimmed.
                // Trimming the front would have been wrong; trimming the back
                // would break the anchor.
                const double MarginSeconds = 10;
                var keepBytes = long.MaxValue;
                if (earliestNeededUtc is DateTime earliest)
                {
                    // WAV is CBR, so seconds convert to bytes exactly. The
                    // margin covers clock jitter.
                    var neededSeconds = (lastSampleUtc - earliest).TotalSeconds + MarginSeconds;
                    keepBytes = neededSeconds > 0 && neededSeconds < long.MaxValue / Math.Max(1, AverageBytesPerSecond)
                        ? (long)(neededSeconds * AverageBytesPerSecond)
                        // Nonsense input (a negative span, or one big enough to
                        // overflow) must not fall back to copying everything -
                        // that is the multi-GB disk storm this whole method
                        // exists to avoid. Keep the margin's worth instead: the
                        // save may come up short of audio, which the pipeline
                        // already pads, rather than freezing the machine.
                        : (long)(MarginSeconds * AverageBytesPerSecond);
                }

                if (_stream is FileStream fileStream)
                {
                    fileStream.Flush(true);
                    // The live file's own RIFF sizes are stale while it is
                    // being written, so the data chunk is located from what
                    // this session knows rather than by parsing the header:
                    // everything past the header is data, and _bytesWritten is
                    // exactly how much of it there is.
                    var dataStart = _stream.Position - _bytesWritten;
                    // Block-align DOWN, like every other byte offset in this
                    // file. keepBytes comes from a fractional second count, so
                    // an unaligned skip lands mid-sample and the tail gets
                    // reinterpreted as 32-bit floats one to three bytes out of
                    // phase: exponent bytes end up in mantissa positions, the
                    // samples come back enormous or NaN, and the channels swap.
                    // That does not sound like a small glitch, it sounds like
                    // full-scale static, and it only bites once a capture is old
                    // enough for the trim to engage at all.
                    var blockAlign = Math.Max(1, _capture.WaveFormat.BlockAlign);
                    var skipBytes = Math.Max(0, _bytesWritten - keepBytes);
                    skipBytes -= skipBytes % blockAlign;
                    // Hand the copy off to the unlocked section below.
                    sourceFileName = fileStream.Name;
                    copyFromOffset = dataStart + skipBytes;
                    copyBytes = _bytesWritten - skipBytes;
                }
                else
                {
                    // MemoryStream - no file to re-open/copy, just write out
                    // whatever's currently buffered. WriteTo copies from
                    // position 0 regardless of the stream's current Position
                    // (which sits at the end, mid-write).
                    var memoryStream = (MemoryStream)_stream;
                    using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    memoryStream.WriteTo(destination);
                }
            }
            catch
            {
                return false;
            }
        }

        // Unlocked: the capture callbacks are free to keep writing while this
        // runs. Nothing here touches session state.
        if (sourceFileName is null) return true;
        try
        {
            using var source = new FileStream(sourceFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            source.Seek(copyFromOffset, SeekOrigin.Begin);
            WriteTailWav(path, source, copyBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Written through a WaveFileWriter with the capture's own WaveFormat so
    // the header carries the right format tag (these captures are 32-bit IEEE
    // float, not PCM) and NAudio finalises the chunk sizes on dispose - a
    // hand-rolled 44-byte header would get both wrong.
    //
    // Background mode, not just a low thread priority: ProcessPriorityClass
    // and ThreadPriority govern CPU only, and this is bound by disk. Windows
    // background mode is the one knob that also drops the thread's I/O
    // priority, which is what keeps this off the queue the game is using.
    private void WriteTailWav(string path, Stream source, long dataBytes)
    {
        var background = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
        try
        {
            using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new WaveFileWriter(destination, _capture.WaveFormat);
            var buffer = new byte[256 * 1024];
            var remaining = dataBytes;
            while (remaining > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) break;
                writer.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            if (background) SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
        }
    }

    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    // Discards audio older than `retention` from a RAM-backed capture's
    // buffer by compacting into a fresh, smaller MemoryStream+WaveFileWriter
    // - keeps exactly this ONE session alive (WASAPI device untouched,
    // FirstSampleUtc just advances) rather than AudioCapturePipeline's
    // earlier approach of stopping this capture and starting a completely
    // fresh one once it grew past a size ceiling. That approach let memory
    // climb toward the ceiling before EVER shrinking back down (looked like
    // a leak over the first several minutes of any armed session, even
    // though it technically capped out eventually) AND meant a save whose
    // window happened to cross a roll boundary had to stitch two captures
    // together instead of reading one straight through (a measured,
    // reported save-time slowdown). Trimming keeps memory flat near
    // `retention` at all times and keeps exactly one capture per source,
    // avoiding both problems at once. No-op for a disk-backed (Full
    // Session) capture - unbounded duration is the whole point there.
    // How far past `retention` a RAM buffer is allowed to run before a
    // compaction is worth its copy. 15s is ~5.7MB per track at a typical
    // 48kHz float mix format.
    private const int CompactSlackSeconds = 15;

    public void TrimTo(TimeSpan retention)
    {
        lock (_lock)
        {
            if (_stream is not MemoryStream oldStream) return;

            var format = _capture.WaveFormat;
            var maxBytes = (long)(retention.TotalSeconds * format.AverageBytesPerSecond);
            maxBytes -= maxBytes % Math.Max(1, format.BlockAlign);
            if (_bytesWritten <= maxBytes) return;

            var trimBytes = _bytesWritten - maxBytes;
            trimBytes -= trimBytes % Math.Max(1, format.BlockAlign);
            if (trimBytes <= 0) return;

            // Compacting copies the WHOLE retained buffer into a fresh
            // MemoryStream, and AudioCapturePipeline calls this every 2s for
            // every live track. At the default 60s retention that is ~25MB
            // copied and re-allocated (large object heap, so gen2) per track
            // per tick - roughly 37MB/s of pure memcpy plus GC across three
            // tracks, all of it under _lock, which is the same lock
            // Capture_OnDataAvailable needs to accept a WASAPI packet. On a
            // machine with no headroom that stalled the capture callbacks
            // long enough for WASAPI to overrun its buffer, and the lost
            // audio showed up in saved clips as crackle and dropouts.
            //
            // So only pay for a copy once there is enough overshoot to be
            // worth one: the buffer floats between retention and retention +
            // CompactSlack (a few MB of extra RAM per track) while the copy
            // runs several times less often.
            var slackBytes = (long)format.AverageBytesPerSecond * CompactSlackSeconds;
            slackBytes -= slackBytes % Math.Max(1, format.BlockAlign);
            if (trimBytes < slackBytes) return;

            try
            {
                _writer.Flush();

                // WaveFileWriter writes a header (44 bytes for plain PCM,
                // larger for float/extensible formats) then the raw sample
                // data - rather than assume a fixed size, derive it from
                // what's actually there: whatever's left after subtracting
                // the PCM byte count this class already tracks separately
                // (_bytesWritten, updated on every real write and silence
                // backfill elsewhere in this class).
                var headerSize = (int)(oldStream.Length - _bytesWritten);
                var oldBuffer = oldStream.GetBuffer();
                var keepBytes = (int)(_bytesWritten - trimBytes);

                var newStream = new MemoryStream(keepBytes + headerSize + 4096);
                var newWriter = new WaveFileWriter(newStream, format);
                newWriter.Write(oldBuffer, headerSize + (int)trimBytes, keepBytes);

                _writer.Dispose();
                oldStream.Dispose();
                _stream = newStream;
                _writer = newWriter;
                _bytesWritten = keepBytes;

                if (FirstSampleUtc is { } first)
                {
                    FirstSampleUtc = first.AddSeconds(trimBytes / (double)format.AverageBytesPerSecond);
                }
            }
            catch (Exception error)
            {
                AppLog.Error($"Audio RAM buffer trim failed: {Title}", error);
            }
        }
    }

    private void Capture_OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Timestamped path (ProcessLoopbackWaveIn, i.e. Game/Chat process
        // captures): every packet carries the exact wall-clock moment its
        // first frame was captured, so bytes are PLACED at their true
        // timeline offset - silence gaps land exactly where the source was
        // silent. This replaces the byte-count deficit heuristic, which
        // drifted hundreds of ms over long sessions (bursty/pre-rolled
        // delivery makes cumulative byte math lie in both directions) and
        // audibly desynced saved clips.
        if (e is TimestampedWaveInEventArgs timestamped)
        {
            if (!_firstSampleSeen)
            {
                _firstSampleSeen = true;
                FirstSampleUtc = timestamped.PacketStartUtc;
            }

            lock (_lock)
            {
                var format = _capture.WaveFormat;
                var expectedBytes = (long)((timestamped.PacketStartUtc - FirstSampleUtc!.Value).TotalSeconds * format.AverageBytesPerSecond);
                expectedBytes -= expectedBytes % Math.Max(1, format.BlockAlign);
                var aheadBytes = expectedBytes - _bytesWritten;
                // ~30ms tolerance: QPC timestamps are exact but packet sizes
                // quantize placement; below this it's jitter, not a gap.
                var toleranceBytes = format.AverageBytesPerSecond * 30 / 1000;
                if (aheadBytes > toleranceBytes)
                {
                    WriteZerosLocked(aheadBytes);
                    if (aheadBytes > format.AverageBytesPerSecond / 4)
                    {
                        AppLog.Info($"Audio capture gap placed from packet timestamps: {Title}, gap={aheadBytes * 1000L / Math.Max(1, format.AverageBytesPerSecond)}ms.");
                    }
                }
                else if (aheadBytes < -toleranceBytes && !_loggedOverlap)
                {
                    _loggedOverlap = true;
                    AppLog.Info($"Audio capture packet overlap (timestamp behind written data): {Title}, behindMs={-aheadBytes * 1000L / Math.Max(1, format.AverageBytesPerSecond)}.");
                }

                _writer.Write(e.Buffer, 0, e.BytesRecorded);
                _bytesWritten += e.BytesRecorded;
                _writer.Flush();
                AccumulatePeakLocked(e.Buffer, e.BytesRecorded);
                LogPlacementDiagnosticLocked(timestamped.PacketStartUtc);
            }

            return;
        }

        var now = MonotonicClock.UtcNow;
        if (!_firstSampleSeen)
        {
            _firstSampleSeen = true;
            FirstSampleUtc = now;
        }

        lock (_lock)
        {
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _bytesWritten += e.BytesRecorded;
            // Deficit check AFTER appending the current buffer, against plain
            // "now" - a pre-write check that subtracts the current buffer's
            // duration hides a steady shortage under the delivery buffer
            // size. Only used for NAudio endpoint/mic captures now; process
            // loopback uses the exact timestamped path above.
            WriteSilenceForDeliveryGapLocked(now);
            _writer.Flush();
            AccumulatePeakLocked(e.Buffer, e.BytesRecorded);
            LogPlacementDiagnosticLocked(now);
        }
    }

    private bool _loggedOverlap;
    private DateTime _nextPlacementDiagUtc = DateTime.MinValue;

    // Loudest absolute sample since the last placement diag - answers "is
    // this capture receiving actual signal or an active-but-silent stream?"
    // (a chat capture once delivered packets for a full hour whose content
    // was pure silence while voice audibly played; nothing in the logs could
    // distinguish that from a genuinely quiet call). 32-bit here is the
    // float mix format every one of these captures uses.
    private float _diagPeak;

    private void AccumulatePeakLocked(byte[] buffer, int bytes)
    {
        if (_capture.WaveFormat.BitsPerSample != 32) return;
        for (var offset = 0; offset + 4 <= bytes; offset += 4)
        {
            var sample = Math.Abs(BitConverter.ToSingle(buffer, offset));
            if (sample > _diagPeak) _diagPeak = sample;
        }
    }

    // Once-a-minute per capture: how far the WAV's written length sits from
    // the wall-clock span it should cover. Near zero = saved clips will be in
    // sync; a growing value points straight at the capture kind responsible.
    private void LogPlacementDiagnosticLocked(DateTime referenceUtc)
    {
        if (referenceUtc < _nextPlacementDiagUtc) return;
        _nextPlacementDiagUtc = referenceUtc + TimeSpan.FromSeconds(60);
        if (FirstSampleUtc is not { } first) return;
        var wallMs = (referenceUtc - first).TotalMilliseconds;
        var writtenMs = BytesToMilliseconds(_bytesWritten);
        var peakDb = _diagPeak > 0 ? 20 * Math.Log10(_diagPeak) : -120;
        _diagPeak = 0;
        AppLog.Debug($"Audio placement diag: {Title}, written={writtenMs / 1000:0.0}s, wall={wallMs / 1000:0.0}s, deficitMs={wallMs - writtenMs:0}, peakDb={peakDb:0}.");

        var clockOffset = MonotonicClock.SystemClockOffset;
        if (!_loggedClockStep && Math.Abs(clockOffset.TotalSeconds) > 2)
        {
            _loggedClockStep = true;
            AppLog.Info($"System clock step detected: wall clock is {clockOffset.TotalMilliseconds:0}ms away from the capture timeline. Capture alignment is unaffected (monotonic timebase).");
        }
    }

    private static bool _loggedClockStep;

    private void WriteZerosLocked(long gapBytes)
    {
        // Never let a silence backfill push the WAV over the 4GiB RIFF cap -
        // WaveFileWriter throws "WAV file too large" there and the throw
        // kills the capture (or fails the snapshot pad, losing the whole
        // track from a save). A quiet process capture can accumulate a huge
        // pad-to-now gap; clamp to remaining capacity and let the pipeline's
        // rollover replace the capture.
        var capacityBytes = uint.MaxValue - 4096L - _bytesWritten;
        gapBytes = Math.Min(gapBytes, Math.Max(0, capacityBytes));
        gapBytes -= gapBytes % Math.Max(1, _capture.WaveFormat.BlockAlign);
        if (gapBytes <= 0) return;

        var zeros = new byte[Math.Min(gapBytes, 64 * 1024)];
        var remaining = gapBytes;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(zeros.Length, remaining);
            _writer.Write(zeros, 0, chunk);
            remaining -= chunk;
        }

        _bytesWritten += gapBytes;
    }

    private long _bytesWritten;

    private double BytesToMilliseconds(long bytes) =>
        bytes * 1000.0 / Math.Max(1, _capture.WaveFormat.AverageBytesPerSecond);

    // Endpoint (speaker) loopback and mic captures deliver a continuous
    // stream - silence included - so their WAV's timeline always matches
    // wall-clock. PROCESS loopback (the Chat tracks) only delivers buffers
    // while the target app is actually rendering audio: every stretch where
    // Discord etc. goes quiet is simply MISSING from the file, making the WAV
    // shorter than the wall-clock span it covers. Both the start- and
    // end-anchored WAV-position math in AudioCapturePipeline assume a
    // continuous timeline, so those holes shifted every chat sound after the
    // first gap to the wrong spot in saved clips (or into apparent silence).
    // Backfill each gap with actual zero samples as it's detected, keeping
    // WAV time == wall time for every capture kind.
    private void WriteSilenceForDeliveryGapLocked(DateTime expectedDataStartUtc, double minGapMs = 300)
    {
        if (FirstSampleUtc is not { } firstSampleUtc) return;
        var expectedMs = (expectedDataStartUtc - firstSampleUtc).TotalMilliseconds;
        var writtenMs = BytesToMilliseconds(_bytesWritten);
        var gapMs = expectedMs - writtenMs;
        // Small jitter between deliveries is normal; only real gaps count.
        // (Snapshots pass a much tighter threshold - there the pad IS the
        // end-anchor, so any unfilled remainder becomes anchor error.)
        if (gapMs < minGapMs) return;

        var format = _capture.WaveFormat;
        var gapBytes = (long)(gapMs / 1000.0 * format.AverageBytesPerSecond);
        gapBytes -= gapBytes % Math.Max(1, format.BlockAlign);
        if (gapBytes <= 0) return;

        WriteZerosLocked(gapBytes);
        if (gapMs >= 300) AppLog.Info($"Audio capture gap backfilled with silence: {Title}, gap={gapMs / 1000.0:0.0}s.");
    }
}
