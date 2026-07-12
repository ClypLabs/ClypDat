using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Eve.App.Services;

// Audio capture (Game/Chat/Microphone process-aware routing, WASAPI loopback/mic
// capture, noise suppression, aligned-track building) extracted out of
// WindowsReplayBuffer so both it and NativeReplayBuffer can own an independent
// instance of the same, already-battle-tested logic instead of duplicating it.
// None of this was ever the source of the segment-rotation gap bug both backends
// exist to work around - it only needs a target wall-clock window handed to it.
[SupportedOSPlatform("windows")]
public sealed class AudioCapturePipeline : IDisposable
{
    private readonly string _bufferFolder;
    private readonly object _lock = new();
    private readonly List<ReplayAudioCapture> _audioCaptures = new();
    private readonly SemaphoreSlim _routeRefreshGate = new(1, 1);
    private Timer? _audioRouteTimer;
    private string _audioRouteKey = string.Empty;
    private ReplayBufferConfig? _activeConfig;

    public AudioCapturePipeline(string bufferFolder)
    {
        _bufferFolder = bufferFolder;
        Directory.CreateDirectory(_bufferFolder);
    }

    public void Start(ReplayBufferConfig config)
    {
        _activeConfig = config;
        StartAudioCaptures(config);
    }

    public void Stop()
    {
        _activeConfig = null;
        _audioRouteTimer?.Dispose();
        _audioRouteTimer = null;
        lock (_lock)
        {
            foreach (var capture in _audioCaptures.ToArray()) StopAudioCapture(capture);
            _audioCaptures.Clear();
        }
    }

    public void PruneOlderThan(DateTime cutoffUtc)
    {
        lock (_lock)
        {
            foreach (var capture in _audioCaptures.Where(capture => capture.EndedAtUtc is not null && capture.EndedAtUtc < cutoffUtc).ToArray())
            {
                _audioCaptures.Remove(capture);
                TryDelete(capture.Path);
            }
        }
    }

    // Builds one aligned WAV per segment window for each of Game/Chat/Microphone and
    // returns the three finished track paths (caller mixes them against its own video).
    // Paths are also added to `snapshots` so the caller can clean them up afterward.
    public async Task<(string GamePath, string ChatPath, string MicrophonePath)> BuildAlignedTracksAsync(
        List<(DateTime StartUtc, double DurationSeconds)> segmentWindows,
        ReplayBufferConfig config,
        List<string> snapshots,
        CancellationToken cancellationToken)
    {
        ReplayAudioCapture[] captures;
        lock (_lock) captures = _audioCaptures.ToArray();

        var gamePath = await BuildAlignedTrackAsync(AudioCaptureKind.Game, captures, segmentWindows, allowMix: true, snapshots, cancellationToken);
        var chatPath = await BuildAlignedTrackAsync(AudioCaptureKind.Chat, captures, segmentWindows, allowMix: true, snapshots, cancellationToken);
        var microphonePath = await BuildAlignedTrackAsync(AudioCaptureKind.Microphone, captures, segmentWindows, allowMix: false, snapshots, cancellationToken, config);
        return (gamePath, chatPath, microphonePath);
    }

    public void Dispose() => Stop();

    private void StartAudioCaptures(ReplayBufferConfig config)
    {
        using var enumerator = new MMDeviceEnumerator();
        var resolvedMicDeviceId = ResolveMicrophoneDeviceId(enumerator, config.MicrophoneDeviceId);
        var routes = ResolveAudioRoutes(config, resolvedMicDeviceId);
        _audioRouteKey = routes.RouteKey;
        AppLog.Info(
            $"Audio route resolved: chat='{config.ChatAudioProcessName}', chatPids={FormatIds(routes.ChatProcessIds)}, exclusions='{string.Join(",", config.GameAudioExcludedProcesses)}', excludedPids={FormatIds(routes.ExcludedProcessIds)}, gamePids={FormatIds(routes.GameProcessIds)}.");
        StopStaleAudioCaptures(routes, resolvedMicDeviceId);
        if (routes.UseProcessRouting)
        {
            foreach (var pid in routes.GameProcessIds)
            {
                if (HasLiveCapture(AudioCaptureKind.Game, pid)) continue;
                try
                {
                    StartProcessLoopbackCapture(AudioCaptureKind.Game, pid, ProcessLoopbackCaptureMode.IncludeTargetProcessTree, "Game Audio");
                }
                catch (Exception error)
                {
                    AppLog.Error($"Game app audio capture failed: pid={pid}", error);
                }
            }

            if (routes.GameProcessIds.Length == 0)
            {
                AppLog.Info("Game audio process routing active but no allowed audio apps found; Game Audio track will be silent.");
            }
        }
        else
        {
            try
            {
                if (!HasLiveCapture(AudioCaptureKind.Game, null))
                {
                    StartLoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), AudioCaptureKind.Game, "Game Audio");
                }
            }
            catch (Exception error)
            {
                AppLog.Error("Game audio capture failed", error);
            }
        }

        foreach (var pid in routes.ChatProcessIds)
        {
            if (HasLiveCapture(AudioCaptureKind.Chat, pid)) continue;
            try
            {
                StartProcessLoopbackCapture(AudioCaptureKind.Chat, pid, ProcessLoopbackCaptureMode.IncludeTargetProcessTree, "Chat Audio");
            }
            catch (Exception error)
            {
                AppLog.Error($"Chat audio capture failed: pid={pid}", error);
            }
        }

        try
        {
            if (!HasLiveCapture(AudioCaptureKind.Microphone, null) && !string.IsNullOrEmpty(resolvedMicDeviceId))
            {
                var micDevice = enumerator.GetDevice(resolvedMicDeviceId);
                StartMicrophoneCapture(micDevice, "Microphone");
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Microphone capture failed", error);
        }

        StartAudioRouteTimer();
    }

    private void StartLoopbackCapture(MMDevice device, AudioCaptureKind kind, string title)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(kind)}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new WasapiLoopbackCapture(device);
        lock (_lock) _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, kind, null, DateTime.UtcNow));
        AppLog.Info($"Audio capture started: {title}, device={device.FriendlyName}.");
    }

    private void StartProcessLoopbackCapture(AudioCaptureKind kind, int processId, ProcessLoopbackCaptureMode mode, string title)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(kind)}_{processId}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new ProcessLoopbackWaveIn(processId, mode);
        lock (_lock) _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, kind, processId, DateTime.UtcNow));
        AppLog.Info($"Audio capture started: {title}, pid={processId}, mode={mode}.");
    }

    private void StartMicrophoneCapture(MMDevice device, string title)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(AudioCaptureKind.Microphone)}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new WasapiCapture(device);
        lock (_lock) _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, AudioCaptureKind.Microphone, null, DateTime.UtcNow, device.ID));
        AppLog.Info($"Audio capture started: {title}, device={device.FriendlyName}.");
    }

    private static string AudioKindPrefix(AudioCaptureKind kind) => kind switch
    {
        AudioCaptureKind.Game => "game",
        AudioCaptureKind.Chat => "chat",
        AudioCaptureKind.Microphone => "microphone",
        _ => "audio"
    };

    private bool HasLiveCapture(AudioCaptureKind kind, int? processId)
    {
        lock (_lock) return _audioCaptures.Any(capture => capture.EndedAtUtc is null && capture.Kind == kind && capture.ProcessId == processId);
    }

    private void StopStaleAudioCaptures(AudioRoutes routes, string resolvedMicDeviceId)
    {
        var wantedChat = routes.ChatProcessIds.ToHashSet();
        var excluded = routes.ExcludedProcessIds.ToHashSet();
        ReplayAudioCapture[] live;
        lock (_lock) live = _audioCaptures.Where(capture => capture.EndedAtUtc is null).ToArray();
        foreach (var capture in live)
        {
            var keep = capture.Kind switch
            {
                AudioCaptureKind.Game when routes.UseProcessRouting => capture.ProcessId is int gamePid && !excluded.Contains(gamePid),
                AudioCaptureKind.Game => capture.ProcessId is null,
                AudioCaptureKind.Chat => capture.ProcessId is int chatPid && wantedChat.Contains(chatPid),
                AudioCaptureKind.Microphone => string.Equals(capture.DeviceId, resolvedMicDeviceId, StringComparison.Ordinal),
                _ => false
            };

            if (!keep || (capture.ProcessId is int processId && !IsProcessAlive(processId)))
            {
                StopAudioCapture(capture);
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void StopAudioCapture(ReplayAudioCapture capture)
    {
        if (capture.EndedAtUtc is not null) return;
        capture.EndedAtUtc = DateTime.UtcNow;
        try
        {
            capture.Session.Dispose();
        }
        catch
        {
            // Stop best effort.
        }

        AppLog.Info($"Audio capture stopped: {capture.Title}, pid={capture.ProcessId?.ToString() ?? "none"}, start={capture.StartedAtUtc:o}, end={capture.EndedAtUtc:o}, bytes={AudioFileLength(capture.Path)}.");
    }

    private void RefreshAudioRoutes()
    {
        if (!_routeRefreshGate.Wait(0)) return;
        try
        {
            var config = _activeConfig;
            if (config is null) return;
            using var enumerator = new MMDeviceEnumerator();
            var resolvedMicDeviceId = ResolveMicrophoneDeviceId(enumerator, config.MicrophoneDeviceId);
            var routes = ResolveAudioRoutes(config, resolvedMicDeviceId);
            if (string.Equals(routes.RouteKey, _audioRouteKey, StringComparison.Ordinal)) return;
            try
            {
                AppLog.Info("Audio route changed; restarting replay audio captures.");
                StartAudioCaptures(config);
            }
            catch (Exception error)
            {
                AppLog.Error("Audio route refresh failed", error);
            }
        }
        finally
        {
            _routeRefreshGate.Release();
        }
    }

    private void StartAudioRouteTimer()
    {
        _audioRouteTimer?.Dispose();
        _audioRouteTimer = new Timer(_ => RefreshAudioRoutes(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    // Resolves to the actual endpoint ID (not the "default" sentinel) so the caller can
    // detect a live default-device swap (e.g. user changes default mic in Windows Sound
    // settings) by comparing this against the device ID the running capture was opened
    // with - WASAPI capture stays bound to whatever device it started on and never
    // follows the system default on its own.
    private static string ResolveMicrophoneDeviceId(MMDeviceEnumerator enumerator, string microphoneDeviceId)
    {
        if (string.IsNullOrWhiteSpace(microphoneDeviceId) || microphoneDeviceId == AudioDeviceOption.DefaultDeviceId)
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID;
            }
            catch
            {
                return string.Empty;
            }
        }

        try
        {
            return enumerator.GetDevice(microphoneDeviceId).ID;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static AudioRoutes ResolveAudioRoutes(ReplayBufferConfig config, string resolvedMicDeviceId)
    {
        // Multi-process apps (Discord, browsers, etc.) share one executable name across
        // several OS processes. StartProcessLoopbackCapture already uses
        // IncludeTargetProcessTree, so capturing more than one PID from the same app
        // captures its audio twice - the reported "chat audio doubled" bug. Only the
        // lowest (typically root/parent) PID is used; its process tree covers the rest.
        var chatPids = ResolveProcessIds(config.ChatAudioProcessName).OrderBy(pid => pid).Take(1).ToArray();
        var useProcessRouting = chatPids.Length > 0 || config.GameAudioExcludedProcesses.Any(name => !string.IsNullOrWhiteSpace(name));
        var exclusionNames = config.GameAudioExcludedProcesses
            .Append(config.ChatAudioProcessName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedPids = exclusionNames
            .SelectMany(ResolveProcessIds)
            .Distinct()
            .OrderBy(pid => pid)
            .ToArray();
        var selfPid = Environment.ProcessId;
        var gamePids = useProcessRouting ? ResolveGameAudioProcessIds(config.GameExecutableName, excludedPids, selfPid) : Array.Empty<int>();
        var key = $"{useProcessRouting}|{string.Join(',', chatPids.OrderBy(pid => pid))}|{string.Join(',', excludedPids)}|{string.Join(',', gamePids)}|{resolvedMicDeviceId}";
        return new AudioRoutes(chatPids.OrderBy(pid => pid).ToArray(), excludedPids, gamePids, useProcessRouting, key, resolvedMicDeviceId);
    }

    private static string FormatIds(IEnumerable<int> ids)
    {
        var values = ids.ToArray();
        return values.Length == 0 ? "none" : string.Join(",", values);
    }

    private static IEnumerable<int> ResolveProcessIds(string processName)
    {
        var normalized = Path.GetFileNameWithoutExtension(processName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return Array.Empty<int>();
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName(normalized))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited) ids.Add(process.Id);
                }
                catch
                {
                    // Process can exit while enumerating.
                }
            }
        }

        return ids;
    }

    // Was "every currently-audio-active process except excluded ones" - meant to
    // catch a game whose audio comes from a differently-named helper process,
    // but in practice swept in ANY other app making noise at the same time
    // (another background app, a stray notification) and mixed it into "Game
    // Audio" alongside the actual game. Prefer the detected game's own PID(s)
    // when they're actually among the currently-active audio sessions - only
    // fall back to the broad sweep when the game isn't known or its own
    // process isn't the one producing sound.
    private static int[] ResolveGameAudioProcessIds(string gameExecutableName, int[] excludedPids, int selfPid)
    {
        var activeAudioPids = ResolveActiveAudioProcessIds()
            .Where(pid => pid != selfPid)
            .Except(excludedPids)
            .ToHashSet();

        var gameNamePids = ResolveProcessIds(gameExecutableName).ToHashSet();
        var matched = activeAudioPids.Where(gameNamePids.Contains).OrderBy(pid => pid).ToArray();
        return matched.Length > 0 ? matched : activeAudioPids.OrderBy(pid => pid).ToArray();
    }

    private static IEnumerable<int> ResolveActiveAudioProcessIds()
    {
        var ids = new HashSet<int>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
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

        return ids;
    }

    private string SnapshotAudioFile(ReplayAudioCapture? capture, DateTime windowStartUtc, double durationSeconds, ICollection<string> snapshots, ReplayBufferConfig? config = null)
    {
        if (capture is null || !IsUsableAudioFile(capture.Path)) return string.Empty;
        var captureEndUtc = capture.EndedAtUtc ?? DateTime.UtcNow;
        var windowEndUtc = windowStartUtc + TimeSpan.FromSeconds(durationSeconds);
        var effectiveStartUtc = capture.EffectiveStartedAtUtc;
        var overlapStartUtc = effectiveStartUtc > windowStartUtc ? effectiveStartUtc : windowStartUtc;
        var overlapEndUtc = captureEndUtc < windowEndUtc ? captureEndUtc : windowEndUtc;
        if (overlapEndUtc <= overlapStartUtc) return string.Empty;

        var sourceSnapshotPath = Path.Combine(_bufferFolder, $"audio_source_{Guid.NewGuid():N}.wav");
        var snapshotPath = Path.Combine(_bufferFolder, $"audio_{Guid.NewGuid():N}.wav");
        try
        {
            var copied = capture.EndedAtUtc is null
                ? capture.Session.SnapshotTo(sourceSnapshotPath)
                : CopyAudioFile(capture.Path, sourceSnapshotPath);
            if (!copied || !IsUsableAudioFile(sourceSnapshotPath))
            {
                TryDelete(sourceSnapshotPath);
                TryDelete(snapshotPath);
                return string.Empty;
            }

            snapshots.Add(sourceSnapshotPath);
            var trimStart = Math.Max(0, (overlapStartUtc - effectiveStartUtc).TotalSeconds);
            var overlapDuration = Math.Max(0, (overlapEndUtc - overlapStartUtc).TotalSeconds);
            var delayMs = Math.Max(0, (int)Math.Round((overlapStartUtc - windowStartUtc).TotalMilliseconds));
            // Noise suppression only makes sense on the mic track - Game/Chat audio
            // is line-level/desktop audio, not a noisy room signal. afftdn's nr= is
            // in dB; the settings UI clamps to 0-30 (its own valid range is wider,
            // but higher than ~20 starts eating into speech on typical mic noise).
            var noiseSuppressionFilter = capture.Kind == AudioCaptureKind.Microphone && config?.MicrophoneNoiseSuppressionEnabled == true
                ? $"afftdn=nr={FormatSeconds(Math.Clamp(config.MicrophoneNoiseSuppressionStrength, 0, 30))},"
                : string.Empty;
            var filters = $"[0:a]atrim=start={FormatSeconds(trimStart)}:duration={FormatSeconds(overlapDuration)},asetpts=PTS-STARTPTS,aresample=48000,{noiseSuppressionFilter}adelay={delayMs}|{delayMs},apad=whole_dur={FormatSeconds(durationSeconds)},atrim=0:{FormatSeconds(durationSeconds)}[out]";
            AppLog.Info($"Replay audio overlap: kind={capture.Kind}, pid={capture.ProcessId?.ToString() ?? "none"}, trim={trimStart:0.###}s, overlap={overlapDuration:0.###}s, delay={delayMs}ms, bytes={AudioFileLength(capture.Path)}.");

            var result = RunProcessAsync("ffmpeg", new[]
            {
                "-y",
                "-v", "error",
                "-i", sourceSnapshotPath,
                "-filter_complex", filters,
                "-map", "[out]",
                "-ac", "2",
                "-c:a", "pcm_s16le",
                snapshotPath
            }, CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode != 0 || !IsUsableAudioFile(snapshotPath))
            {
                TryDelete(snapshotPath);
                return string.Empty;
            }

            snapshots.Add(snapshotPath);
            return snapshotPath;
        }
        catch
        {
            TryDelete(sourceSnapshotPath);
            TryDelete(snapshotPath);
            return string.Empty;
        }
    }

    private async Task<string> BuildAlignedTrackAsync(
        AudioCaptureKind kind,
        ReplayAudioCapture[] captures,
        List<(DateTime StartUtc, double DurationSeconds)> segmentWindows,
        bool allowMix,
        List<string> snapshots,
        CancellationToken cancellationToken,
        ReplayBufferConfig? config = null)
    {
        var segmentClips = new List<string>();
        foreach (var (startUtc, durationSeconds) in segmentWindows)
        {
            var endUtc = startUtc + TimeSpan.FromSeconds(durationSeconds);
            var overlapping = captures
                .Where(capture => capture.Kind == kind && AudioCaptureOverlaps(capture, startUtc, endUtc))
                .ToArray();
            if (!allowMix && overlapping.Length > 1)
            {
                overlapping = new[] { overlapping[^1] };
            }

            var clipPaths = overlapping
                .Select(capture => SnapshotAudioFile(capture, startUtc, durationSeconds, snapshots, config))
                .Where(IsUsableAudioFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string segmentClip;
            if (clipPaths.Length == 0)
            {
                segmentClip = await CreateSilentClipAsync(durationSeconds, snapshots, cancellationToken);
            }
            else if (clipPaths.Length == 1)
            {
                segmentClip = clipPaths[0];
            }
            else
            {
                segmentClip = await MixClipsAsync(clipPaths, durationSeconds, snapshots, cancellationToken);
            }

            segmentClips.Add(segmentClip);
        }

        if (segmentClips.Count == 0)
        {
            var totalDuration = segmentWindows.Sum(window => window.DurationSeconds);
            return await CreateSilentClipAsync(totalDuration, snapshots, cancellationToken);
        }

        return segmentClips.Count == 1
            ? segmentClips[0]
            : await ConcatClipsAsync(segmentClips, snapshots, cancellationToken);
    }

    private async Task<string> CreateSilentClipAsync(double durationSeconds, ICollection<string> snapshots, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_bufferFolder, $"audio_silent_{Guid.NewGuid():N}.wav");
        var result = await RunProcessAsync("ffmpeg", new[]
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-t", FormatSeconds(durationSeconds), "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
            "-c:a", "pcm_s16le",
            path
        }, cancellationToken);
        if (result.ExitCode != 0 || !IsUsableAudioFile(path))
        {
            AppLog.Error($"Silent clip generation failed: duration={durationSeconds:0.###}s, error={result.Error}");
        }

        snapshots.Add(path);
        return path;
    }

    private async Task<string> MixClipsAsync(IReadOnlyList<string> clipPaths, double durationSeconds, ICollection<string> snapshots, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_bufferFolder, $"audio_mix_{Guid.NewGuid():N}.wav");
        var args = new List<string> { "-y", "-v", "error" };
        foreach (var clipPath in clipPaths)
        {
            args.AddRange(new[] { "-i", clipPath });
        }

        var labels = string.Concat(Enumerable.Range(0, clipPaths.Count).Select(index => $"[{index}:a]"));
        args.AddRange(new[]
        {
            "-filter_complex", $"{labels}amix=inputs={clipPaths.Count}:normalize=0,atrim=0:{FormatSeconds(durationSeconds)},asetpts=PTS-STARTPTS[out]",
            "-map", "[out]",
            "-c:a", "pcm_s16le",
            path
        });
        var result = await RunProcessAsync("ffmpeg", args, cancellationToken);
        if (result.ExitCode != 0 || !IsUsableAudioFile(path))
        {
            AppLog.Error($"Audio mix failed: clips={clipPaths.Count}, error={result.Error}");
            return await CreateSilentClipAsync(durationSeconds, snapshots, cancellationToken);
        }

        snapshots.Add(path);
        return path;
    }

    private async Task<string> ConcatClipsAsync(IReadOnlyList<string> clipPaths, ICollection<string> snapshots, CancellationToken cancellationToken)
    {
        var concatPath = Path.Combine(_bufferFolder, $"audio_concat_{Guid.NewGuid():N}.txt");
        var outputPath = Path.Combine(_bufferFolder, $"audio_concat_{Guid.NewGuid():N}.wav");
        await File.WriteAllLinesAsync(concatPath, clipPaths.Select(path => $"file '{EscapeConcatPath(path)}'"), cancellationToken);
        try
        {
            var result = await RunProcessAsync("ffmpeg", new[] { "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", concatPath, "-c", "copy", outputPath }, cancellationToken);
            if (result.ExitCode != 0 || !IsUsableAudioFile(outputPath))
            {
                AppLog.Error($"Audio concat failed: clips={clipPaths.Count}, error={result.Error}");
            }
        }
        finally
        {
            TryDelete(concatPath);
        }

        snapshots.Add(outputPath);
        return outputPath;
    }

    public static string FormatSeconds(double seconds)
    {
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal);
    }

    public static async Task<bool> WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        const int attempts = 5;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (stream.Length > 0) return true;
                }
            }
            catch (IOException)
            {
                // Likely still locked (e.g. AV real-time scan) - retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }

            if (attempt < attempts - 1) await Task.Delay(150, cancellationToken);
        }

        return false;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup best effort.
        }
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        using var process = StartProcess(fileName, args);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static Process StartProcess(string fileName, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        try
        {
            // Concat/mux ffmpeg runs at full CPU priority by default, competing
            // directly with whatever game is running - BelowNormal still gets
            // scheduled whenever a core is actually free, it just yields under
            // contention.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Priority is a nice-to-have; never let it block starting the process.
        }

        return process;
    }

    private static bool CopyAudioFile(string source, string destination)
    {
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            input.CopyTo(output);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsUsableAudioFile(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 44;
    }

    private static long AudioFileLength(string path)
    {
        return IsUsableAudioFile(path) ? new FileInfo(path).Length : 0;
    }

    private static bool AudioCaptureOverlaps(ReplayAudioCapture capture, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        var captureEndUtc = capture.EndedAtUtc ?? DateTime.UtcNow;
        return capture.EffectiveStartedAtUtc < windowEndUtc && captureEndUtc > windowStartUtc;
    }

    public sealed record AudioRoutes(int[] ChatProcessIds, int[] ExcludedProcessIds, int[] GameProcessIds, bool UseProcessRouting, string RouteKey, string MicrophoneDeviceId);

    public enum AudioCaptureKind
    {
        Game,
        Chat,
        Microphone
    }

    private sealed class ReplayAudioCapture
    {
        public ReplayAudioCapture(AudioCaptureSession session, string path, string title, AudioCaptureKind kind, int? processId, DateTime startedAtUtc, string? deviceId = null)
        {
            Session = session;
            Path = path;
            Title = title;
            Kind = kind;
            ProcessId = processId;
            StartedAtUtc = startedAtUtc;
            DeviceId = deviceId;
        }

        public AudioCaptureSession Session { get; }
        public string Path { get; }
        public string Title { get; }
        public AudioCaptureKind Kind { get; }
        public int? ProcessId { get; }
        public string? DeviceId { get; }
        public DateTime StartedAtUtc { get; }
        public DateTime? EndedAtUtc { get; set; }

        // Prefer the first-real-sample timestamp once known - closes the gap between
        // when StartRecording() was called and when the device actually began
        // delivering audio, which otherwise makes Game Audio (WASAPI endpoint
        // loopback) lead the video by however much longer its startup latency is
        // versus Chat/Microphone.
        public DateTime EffectiveStartedAtUtc => Session.FirstSampleUtc ?? StartedAtUtc;
    }
}
