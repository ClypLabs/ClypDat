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
//
// Chat apps and microphones can each have multiple configured sources - every
// configured chat app gets its own output track (not merged into one "Chat
// Audio" track), and every configured microphone gets its own track too. Game
// stays a single track (its own multi-process audio, if any, is still mixed
// together - there's no user-facing concept of "multiple games").
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
            // StopAudioCapture only closes the file handle (EndedAtUtc marks it
            // stale for PruneOlderThan, which callers that keep running after a
            // route change rely on to actually delete it later). Once the whole
            // session is stopping, nothing will call PruneOlderThan again for
            // these, so the raw WAV files must be deleted here or they sit in
            // the buffer folder forever.
            foreach (var capture in _audioCaptures.ToArray())
            {
                StopAudioCapture(capture);
                TryDelete(capture.Path);
            }
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

    // Builds one aligned WAV per segment window for Game (always exactly one
    // track), each configured chat app (one track per app), and each configured
    // microphone (one track per device) - returns the finished (Label, Path)
    // pairs in the order they should appear as audio streams in the output.
    // Paths are also added to `snapshots` so the caller can clean them up
    // afterward. If nothing is configured for chat/mic, no track is emitted for
    // that kind at all (no silent placeholder) - the output simply has fewer
    // audio streams.
    public async Task<List<(string Label, string Path)>> BuildAlignedTracksAsync(
        List<(DateTime StartUtc, double DurationSeconds)> segmentWindows,
        ReplayBufferConfig config,
        List<string> snapshots,
        CancellationToken cancellationToken)
    {
        ReplayAudioCapture[] captures;
        lock (_lock) captures = _audioCaptures.ToArray();

        var tracks = new List<(string Label, string Path)>();

        var gamePath = await BuildAlignedTrackAsync(AudioCaptureKind.Game, captures, null, segmentWindows, allowMix: true, snapshots, cancellationToken);
        tracks.Add(("Game Audio", gamePath));

        var chatAppNames = NormalizedList(config.ChatAudioProcessNames);
        foreach (var appName in chatAppNames)
        {
            var path = await BuildAlignedTrackAsync(AudioCaptureKind.Chat, captures, appName, segmentWindows, allowMix: true, snapshots, cancellationToken);
            var label = chatAppNames.Count > 1 ? $"Chat Audio - {appName}" : "Chat Audio";
            tracks.Add((label, path));
        }

        // config.MicrophoneDeviceIds carries the raw configured value (e.g. the
        // literal "default" placeholder), but StartAudioCaptures resolves that
        // through ResolveMicrophoneDeviceIds into the real WASAPI endpoint ID
        // before using it as a capture's SourceKey. Matching against the raw,
        // unresolved value here meant "default" never equalled the real GUID
        // SourceKey, so the mic track always fell back to a silent clip.
        using var micEnumerator = new MMDeviceEnumerator();
        var micIds = ResolveMicrophoneDeviceIds(micEnumerator, config.MicrophoneDeviceIds);
        var micIndex = 0;
        foreach (var micId in micIds)
        {
            micIndex++;
            var path = await BuildAlignedTrackAsync(AudioCaptureKind.Microphone, captures, micId, segmentWindows, allowMix: false, snapshots, cancellationToken, config);
            var label = micIds.Length > 1 ? $"Microphone {micIndex}" : "Microphone";
            tracks.Add((label, path));
        }

        return tracks;
    }

    public void Dispose() => Stop();

    private void StartAudioCaptures(ReplayBufferConfig config)
    {
        using var enumerator = new MMDeviceEnumerator();
        var resolvedMicDeviceIds = ResolveMicrophoneDeviceIds(enumerator, config.MicrophoneDeviceIds);
        var routes = ResolveAudioRoutes(config, resolvedMicDeviceIds);
        _audioRouteKey = routes.RouteKey;
        AppLog.Info(
            $"Audio route resolved: chatApps={routes.ChatRoutes.Length}, exclusions='{string.Join(",", config.GameAudioExcludedProcesses)}', excludedPids={FormatIds(routes.ExcludedProcessIds)}, gamePids={FormatIds(routes.GameProcessIds)}, mics={resolvedMicDeviceIds.Length}.");
        StopStaleAudioCaptures(routes);
        if (routes.UseProcessRouting)
        {
            foreach (var pid in routes.GameProcessIds)
            {
                if (HasLiveCapture(AudioCaptureKind.Game, pid.ToString(CultureInfo.InvariantCulture))) continue;
                try
                {
                    StartProcessLoopbackCapture(AudioCaptureKind.Game, pid, ProcessLoopbackCaptureMode.IncludeTargetProcessTree, "Game Audio", pid.ToString(CultureInfo.InvariantCulture));
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
                if (!HasLiveCapture(AudioCaptureKind.Game, "default"))
                {
                    StartLoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), AudioCaptureKind.Game, "Game Audio", "default");
                }
            }
            catch (Exception error)
            {
                AppLog.Error("Game audio capture failed", error);
            }
        }

        foreach (var route in routes.ChatRoutes)
        {
            if (HasLiveCapture(AudioCaptureKind.Chat, route.AppName)) continue;
            try
            {
                StartProcessLoopbackCapture(AudioCaptureKind.Chat, route.ProcessId, ProcessLoopbackCaptureMode.IncludeTargetProcessTree, $"Chat Audio - {route.AppName}", route.AppName);
            }
            catch (Exception error)
            {
                AppLog.Error($"Chat app audio capture failed: app={route.AppName}, pid={route.ProcessId}", error);
            }
        }

        foreach (var micId in resolvedMicDeviceIds)
        {
            if (HasLiveCapture(AudioCaptureKind.Microphone, micId)) continue;
            try
            {
                var micDevice = enumerator.GetDevice(micId);
                StartMicrophoneCapture(micDevice, $"Microphone - {micDevice.FriendlyName}", micId);
            }
            catch (Exception error)
            {
                AppLog.Error($"Microphone capture failed: device={micId}", error);
            }
        }

        StartAudioRouteTimer();
    }

    private void StartLoopbackCapture(MMDevice device, AudioCaptureKind kind, string title, string sourceKey)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(kind)}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new WasapiLoopbackCapture(device);
        lock (_lock) _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, kind, null, DateTime.UtcNow, sourceKey));
        AppLog.Info($"Audio capture started: {title}, device={device.FriendlyName}.");
    }

    private void StartProcessLoopbackCapture(AudioCaptureKind kind, int processId, ProcessLoopbackCaptureMode mode, string title, string sourceKey)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(kind)}_{processId}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new ProcessLoopbackWaveIn(processId, mode);
        lock (_lock) _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, kind, processId, DateTime.UtcNow, sourceKey));
        AppLog.Info($"Audio capture started: {title}, pid={processId}, mode={mode}.");
    }

    private void StartMicrophoneCapture(MMDevice device, string title, string sourceKey)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(AudioCaptureKind.Microphone)}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new WasapiCapture(device);
        lock (_lock) _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, AudioCaptureKind.Microphone, null, DateTime.UtcNow, sourceKey, device.ID));
        AppLog.Info($"Audio capture started: {title}, device={device.FriendlyName}.");
    }

    private static string AudioKindPrefix(AudioCaptureKind kind) => kind switch
    {
        AudioCaptureKind.Game => "game",
        AudioCaptureKind.Chat => "chat",
        AudioCaptureKind.Microphone => "microphone",
        _ => "audio"
    };

    private bool HasLiveCapture(AudioCaptureKind kind, string sourceKey)
    {
        lock (_lock) return _audioCaptures.Any(capture => capture.EndedAtUtc is null && capture.Kind == kind && string.Equals(capture.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase));
    }

    private void StopStaleAudioCaptures(AudioRoutes routes)
    {
        var wantedChat = routes.ChatRoutes.ToDictionary(route => route.AppName, route => route.ProcessId, StringComparer.OrdinalIgnoreCase);
        var wantedMics = routes.MicrophoneDeviceIds.ToHashSet(StringComparer.Ordinal);
        var excluded = routes.ExcludedProcessIds.ToHashSet();
        ReplayAudioCapture[] live;
        lock (_lock) live = _audioCaptures.Where(capture => capture.EndedAtUtc is null).ToArray();
        foreach (var capture in live)
        {
            var keep = capture.Kind switch
            {
                AudioCaptureKind.Game when routes.UseProcessRouting => capture.ProcessId is int gamePid && !excluded.Contains(gamePid),
                AudioCaptureKind.Game => capture.ProcessId is null,
                // Keep only if this app is still configured AND its currently-resolved
                // pid still matches - if the app restarted with a new pid, this capture
                // is stale and a fresh one will start for the new pid.
                AudioCaptureKind.Chat => wantedChat.TryGetValue(capture.SourceKey, out var wantedPid) && wantedPid == capture.ProcessId,
                AudioCaptureKind.Microphone => wantedMics.Contains(capture.SourceKey),
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
            var resolvedMicDeviceIds = ResolveMicrophoneDeviceIds(enumerator, config.MicrophoneDeviceIds);
            var routes = ResolveAudioRoutes(config, resolvedMicDeviceIds);
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

    private static string[] ResolveMicrophoneDeviceIds(MMDeviceEnumerator enumerator, IReadOnlyList<string>? configuredIds)
    {
        var configured = NormalizedList(configuredIds);
        var resolved = new List<string>();
        foreach (var id in configured)
        {
            var resolvedId = ResolveMicrophoneDeviceId(enumerator, id);
            if (!string.IsNullOrEmpty(resolvedId)) resolved.Add(resolvedId);
        }

        return resolved.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static List<string> NormalizedList(IReadOnlyList<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AudioRoutes ResolveAudioRoutes(ReplayBufferConfig config, string[] resolvedMicDeviceIds)
    {
        var chatAppNames = NormalizedList(config.ChatAudioProcessNames);

        // Multi-process apps (Discord, browsers, etc.) share one executable name across
        // several OS processes. StartProcessLoopbackCapture already uses
        // IncludeTargetProcessTree, so capturing more than one PID from the same app
        // captures its audio twice - the reported "chat audio doubled" bug. Only the
        // lowest (typically root/parent) PID is used per app; its process tree covers
        // the rest.
        var chatRoutes = new List<ChatRoute>();
        foreach (var appName in chatAppNames)
        {
            var pids = ResolveProcessIds(appName).OrderBy(pid => pid).ToArray();
            if (pids.Length > 0) chatRoutes.Add(new ChatRoute(appName, pids[0]));
        }

        var useProcessRouting = chatRoutes.Count > 0 || config.GameAudioExcludedProcesses.Any(name => !string.IsNullOrWhiteSpace(name));
        var exclusionNames = config.GameAudioExcludedProcesses
            .Concat(chatAppNames)
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
        var key = $"{useProcessRouting}|{string.Join(',', chatRoutes.OrderBy(route => route.AppName, StringComparer.OrdinalIgnoreCase).Select(route => $"{route.AppName}:{route.ProcessId}"))}|{string.Join(',', excludedPids)}|{string.Join(',', gamePids)}|{string.Join(',', resolvedMicDeviceIds.OrderBy(id => id, StringComparer.Ordinal))}";
        return new AudioRoutes(chatRoutes.ToArray(), excludedPids, gamePids, useProcessRouting, key, resolvedMicDeviceIds);
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
        string? sourceKey,
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
                .Where(capture => capture.Kind == kind
                    && (sourceKey is null || string.Equals(capture.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
                    && AudioCaptureOverlaps(capture, startUtc, endUtc))
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

    public sealed record ChatRoute(string AppName, int ProcessId);

    public sealed record AudioRoutes(ChatRoute[] ChatRoutes, int[] ExcludedProcessIds, int[] GameProcessIds, bool UseProcessRouting, string RouteKey, string[] MicrophoneDeviceIds);

    public enum AudioCaptureKind
    {
        Game,
        Chat,
        Microphone
    }

    private sealed class ReplayAudioCapture
    {
        public ReplayAudioCapture(AudioCaptureSession session, string path, string title, AudioCaptureKind kind, int? processId, DateTime startedAtUtc, string sourceKey, string? deviceId = null)
        {
            Session = session;
            Path = path;
            Title = title;
            Kind = kind;
            ProcessId = processId;
            StartedAtUtc = startedAtUtc;
            SourceKey = sourceKey;
            DeviceId = deviceId;
        }

        public AudioCaptureSession Session { get; }
        public string Path { get; }
        public string Title { get; }
        public AudioCaptureKind Kind { get; }
        public int? ProcessId { get; }
        // Identity of which configured source this capture belongs to - a game
        // pid (or "default" for whole-desktop loopback), a configured chat app
        // name, or a microphone device id. Used to match live captures back to
        // Settings when resolving routes/building tracks.
        public string SourceKey { get; }
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
