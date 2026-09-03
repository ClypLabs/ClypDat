using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public sealed record ActiveAudioProcess(string Name, int ProcessId, string ExecutablePath);

// Audio capture (Game/Chat/Microphone process-aware routing, WASAPI loopback/mic
// capture, aligned-track building) extracted out of
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
    // Serialises the per-capture WAV snapshot copies (see
    // CreateSourceSnapshotAsync) and caps how many ffmpeg processes a single
    // save may have in flight at once (see RunGatedProcessAsync). Static so
    // the limits hold across pipelines rather than per instance.
    //
    // The cap matters most for a Full Session finalize: its window is chunked
    // into 60s segments, so building every segment of every track at once -
    // which is what an unbounded Task.WhenAll does - meant hundreds of
    // concurrent ffmpeg processes for a multi-hour session. Two keeps the
    // overlap that makes saves quick without letting the machine fall over.
    private static readonly SemaphoreSlim SourceSnapshotGate = new(1, 1);
    private static readonly SemaphoreSlim FfmpegGate = new(2, 2);
    private readonly List<ReplayAudioCapture> _audioCaptures = new();
    private readonly SemaphoreSlim _routeRefreshGate = new(1, 1);
    private readonly DefaultMicrophoneWatcher _defaultMicrophoneWatcher;
    private Timer? _audioRouteTimer;
    private string _audioRouteKey = string.Empty;
    private int _stableRoutePasses;
    private TimeSpan _routeInterval = TimeSpan.FromSeconds(2);
    private ReplayBufferConfig? _activeConfig;
    private string _micFilterSignature = string.Empty;

    public AudioCapturePipeline(string bufferFolder)
    {
        _bufferFolder = bufferFolder;
        Directory.CreateDirectory(_bufferFolder);
        _defaultMicrophoneWatcher = new DefaultMicrophoneWatcher(RefreshAudioRoutes);
    }

    public void Start(ReplayBufferConfig config)
    {
        _activeConfig = config;
        StartAudioCaptures(config);
    }

    // deleteCaptureFiles: false when a background full-session finalize still
    // needs the raw WAVs - the finalize job takes ownership (via the
    // CaptureSetSnapshot it was handed) and deletes them itself when done.
    public void Stop(bool deleteCaptureFiles = true)
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
                if (deleteCaptureFiles) TryDelete(capture.Path);
            }
            _audioCaptures.Clear();
        }
    }

    // Opaque handle over the current capture set, for a background finalize
    // that must keep building tracks after Stop() has cleared the live list.
    public sealed class CaptureSetSnapshot
    {
        internal CaptureSetSnapshot(ReplayAudioCapture[] captures) => Captures = captures;
        internal ReplayAudioCapture[] Captures { get; }
        public IReadOnlyList<string> FilePaths => Captures.Select(capture => capture.Path).ToArray();
    }

    public CaptureSetSnapshot SnapshotCaptures()
    {
        lock (_lock) return new CaptureSetSnapshot(_audioCaptures.ToArray());
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
    internal async Task<List<(string Label, string Path)>> BuildAlignedTracksAsync(
        List<(DateTime StartUtc, double DurationSeconds)> segmentWindows,
        ReplayBufferConfig config,
        List<string> snapshots,
        CancellationToken cancellationToken,
        CaptureSetSnapshot? capturesOverride = null,
        AudioSnapshotPurpose snapshotPurpose = AudioSnapshotPurpose.InteractiveReplay)
    {
        ReplayAudioCapture[] captures;
        if (capturesOverride is not null)
        {
            captures = capturesOverride.Captures;
        }
        else
        {
            lock (_lock) captures = _audioCaptures.ToArray();
        }

        // Shared across every track/segment of this one save - see
        // GetOrCreateSourceSnapshotAsync for why this must be per-save, not
        // per-chunk. Lazy<Task<...>> ensures a given capture's (potentially
        // multi-GB) source snapshot copy only ever runs once even though every
        // track/segment below now builds concurrently instead of sequentially -
        // without it, concurrent segments referencing the same capture would
        // race into copying the same source WAV multiple times.
        var sourceSnapshotCache = new ConcurrentDictionary<ReplayAudioCapture, Lazy<Task<SourceSnapshot?>>>();

        // The oldest instant any segment of any track will ask for. Everything
        // in a capture older than this is unreachable for this save, so the
        // snapshot copies can stop at it instead of duplicating the whole
        // multi-GB session WAV. Null when there are no windows, which keeps
        // the copy-everything behaviour.
        var earliestNeededUtc = segmentWindows.Count > 0
            ? segmentWindows.Min(window => window.StartUtc)
            : (DateTime?)null;

        var trackJobs = new List<(string Label, Task<string> PathTask)>
        {
            ("Game Audio", BuildAlignedTrackAsync(AudioCaptureKind.Game, captures, null, segmentWindows, allowMix: true, snapshots, sourceSnapshotCache, earliestNeededUtc, snapshotPurpose, cancellationToken, volumePercent: Math.Clamp(config.GameAudioVolumePercent, 0, 150)))
        };

        // Derive application lanes from current configuration, not from the
        // capture list. Ended routes from an earlier refresh must not resurrect
        // disabled apps in a newly saved clip.
        var additionalApps = AudioProcessIdentity.NormalizeDictionary(config.AdditionalAudioProcesses);
        var configuredApps = AudioProcessIdentity.NormalizeList(config.ChatAudioProcessNames)
            .Concat(additionalApps.Keys)
            .Select(AudioProcessIdentity.Normalize)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var orderedApps = AudioProcessIdentity.OrderForRecording(configuredApps).ToArray();

        // Social/chat apps come before microphones; other enabled apps follow
        // alphabetically. Each application remains its own editable track.
        void AddApplicationTracks(IEnumerable<string> names)
        {
            foreach (var appName in names)
            {
                var gain = AudioProcessIdentity.TryGetValue(additionalApps, appName, out var value) ? Math.Clamp(value, 0, 150) : 100;
                trackJobs.Add((appName, BuildAlignedTrackAsync(AudioCaptureKind.Chat, captures, appName, segmentWindows, allowMix: false, snapshots, sourceSnapshotCache, earliestNeededUtc, snapshotPurpose, cancellationToken, volumePercent: gain)));
            }
        }

        AddApplicationTracks(orderedApps.Where(AudioProcessIdentity.IsSocial));

        // SourceKey is the saved logical selection, while DeviceId is the
        // physical WASAPI endpoint. Keeping "default" as the former lets an
        // aligned track contain audio from both sides of a Windows default swap.
        using var micEnumerator = new MMDeviceEnumerator();
        var micIds = ResolveMicrophoneRoutes(micEnumerator, config.MicrophoneDeviceIds)
            .Select(route => route.SourceKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var micIndex = 0;
        foreach (var micId in micIds)
        {
            micIndex++;
            var label = micIds.Length > 1 ? $"Microphone {micIndex}" : "Microphone";
            trackJobs.Add((label, BuildAlignedTrackAsync(AudioCaptureKind.Microphone, captures, micId, segmentWindows, allowMix: false, snapshots, sourceSnapshotCache, earliestNeededUtc, snapshotPurpose, cancellationToken, config, Math.Clamp(config.MicrophoneVolumePercent, 0, 150))));
        }

        AddApplicationTracks(orderedApps.Where(name => !AudioProcessIdentity.IsSocial(name)));

        // Game/Chat/Mic tracks are independent of each other, so build them
        // concurrently instead of one after another - this was the main
        // avoidable serialization tax on every clip save (each track spawns
        // several ffmpeg processes of its own, see BuildAlignedTrackAsync).
        await Task.WhenAll(trackJobs.Select(job => job.PathTask));
        var tracks = trackJobs.Select(job => (job.Label, job.PathTask.Result)).ToList();

        // Spotify often keeps a render session alive while paused, so its
        // configured route produces a correctly aligned WAV full of generated
        // silence. Do not mux that empty lane. Other app tracks deliberately
        // keep their current placeholder behaviour.
        foreach (var track in tracks.Where(track => AudioProcessIdentity.Equals(track.Label, "Spotify")).ToArray())
        {
            if (!IsSilentWaveFile(track.Result)) continue;
            AppLog.Info($"Spotify track omitted because requested window is silent: path={track.Result}.");
            tracks.Remove(track);
        }

        return tracks;
    }

    public void Dispose()
    {
        Stop();
        _defaultMicrophoneWatcher.Dispose();
    }

    private void StartAudioCaptures(ReplayBufferConfig config)
    {
        using var scope = new RouteScope();
        var microphoneRoutes = ResolveMicrophoneRoutes(scope.Enumerator, config.MicrophoneDeviceIds);
        var routes = ResolveAudioRoutes(config, microphoneRoutes, scope);
        _audioRouteKey = routes.RouteKey;
        AppLog.Info(
            $"Audio route resolved: chatApps={routes.ChatRoutes.Length}, exclusions='{string.Join(",", config.GameAudioExcludedProcesses)}', excludedPids={FormatIds(routes.ExcludedProcessIds)}, gamePids={FormatIds(routes.GameProcessIds)}, mics={microphoneRoutes.Length}.");
        StopStaleAudioCaptures(routes);
        // StopStaleAudioCaptures keeps a microphone whose device is still
        // selected, which is right for a device change and wrong for a filter
        // change - the filter lives inside the capture object, so the only way
        // to apply a new one is to build a new capture.
        var micFilterSignature = MicrophoneFilterSignature(config);
        if (!string.Equals(_micFilterSignature, micFilterSignature, StringComparison.Ordinal))
        {
            if (_micFilterSignature.Length > 0)
            {
                AppLog.Info($"Microphone filtering changed ({_micFilterSignature} -> {micFilterSignature}); restarting microphone captures.");
                ReplayAudioCapture[] mics;
                lock (_lock) mics = _audioCaptures.Where(capture => capture.Kind == AudioCaptureKind.Microphone && capture.EndedAtUtc is null).ToArray();
                foreach (var capture in mics) StopAudioCapture(capture);
            }

            _micFilterSignature = micFilterSignature;
        }

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
                    StartLoopbackCapture(scope.Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), AudioCaptureKind.Game, "Game Audio", "default");
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
                StartProcessLoopbackCapture(AudioCaptureKind.Chat, route.ProcessId, ProcessLoopbackCaptureMode.IncludeTargetProcessTree, route.AppName, route.AppName);
            }
            catch (Exception error)
            {
                AppLog.Error($"Chat app audio capture failed: app={route.AppName}, pid={route.ProcessId}", error);
            }
        }

        foreach (var route in routes.MicrophoneRoutes)
        {
            if (string.IsNullOrEmpty(route.DeviceId) || HasLiveCapture(AudioCaptureKind.Microphone, route.SourceKey)) continue;
            try
            {
                var micDevice = scope.Enumerator.GetDevice(route.DeviceId);
                StartMicrophoneCapture(micDevice, $"Microphone - {micDevice.FriendlyName}", route.SourceKey, config);
            }
            catch (Exception error)
            {
                AppLog.Error($"Microphone capture failed: device={route.DeviceId}", error);
            }
        }

        StartAudioRouteTimer();
    }

    private void StartLoopbackCapture(MMDevice device, AudioCaptureKind kind, string title, string sourceKey)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(kind)}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new WasapiLoopbackCapture(device);
        lock (_lock) _audioCaptures.Add(new ReplayAudioCapture(StartSession(capture, path, title), path, title, kind, null, MonotonicClock.UtcNow, sourceKey));
        AppLog.Debug($"Audio capture started: {title}, device={device.FriendlyName}.");
    }

    private void StartProcessLoopbackCapture(AudioCaptureKind kind, int processId, ProcessLoopbackCaptureMode mode, string title, string sourceKey)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(kind)}_{processId}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new ProcessLoopbackWaveIn(processId, mode);
        lock (_lock) _audioCaptures.Add(new ReplayAudioCapture(StartSession(capture, path, title), path, title, kind, processId, MonotonicClock.UtcNow, sourceKey));
        AppLog.Debug($"Audio capture started: {title}, pid={processId}, mode={mode}.");
    }

    private void StartMicrophoneCapture(MMDevice device, string title, string sourceKey, ReplayBufferConfig config)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(AudioCaptureKind.Microphone)}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        // Noise suppression sits between the device and the session writer, so
        // the replay buffer holds already-filtered audio. Wrap returns the raw
        // capture untouched whenever it cannot run - a microphone that records
        // unfiltered beats a microphone that does not record.
        var capture = MicrophoneNoiseSuppression.Wrap(
            new MicrophoneWaveIn(device),
            config.MicrophoneNoiseSuppressionEnabled,
            config.MicrophoneNoiseGateThresholdDb,
            device.FriendlyName);
        lock (_lock) _audioCaptures.Add(new ReplayAudioCapture(StartSession(capture, path, title), path, title, AudioCaptureKind.Microphone, null, MonotonicClock.UtcNow, sourceKey, device.ID));
        AppLog.Debug($"Audio capture started: {title}, device={device.FriendlyName}, denoise={config.MicrophoneNoiseSuppressionEnabled}.");
    }

    // Full Session needs disk (a recording that can run for hours can't
    // reasonably live entirely in RAM, and losing it to a crash would be far
    // worse than the disk-write cost) - the plain replay-buffer window is
    // capped at 20 minutes and only needs to survive until the next save, so
    // it goes to RAM instead: no continuous disk writes for the common case
    // (recording armed, nothing saved yet), which used to run the whole time
    // the buffer was armed regardless of whether anything was ever saved.
    // `path` is still passed through even for the in-memory case - nothing
    // is ever written there, but ReplayAudioCapture.Path is also used as a
    // plain nominal identifier elsewhere (logging, TryDelete no-ops).
    // Reverted to always-disk. RAM-backed capture (StartInMemory below) was
    // the suspected cause of a severe, rapidly-repeating managed-heap churn -
    // managedMb swinging from ~200MB to 2-3GB within seconds, driving
    // frequent multi-second blocking GC pauses that showed up as capture
    // stutter/freezing - after the video ring buffer was conclusively ruled
    // out as the source (its own byte total stayed flat at ~80-95MB across
    // the exact same window the managed heap was swinging wildly). Left the
    // RAM infrastructure (StartInMemory/TrimMemoryCaptures/IsMemoryBacked)
    // in place rather than ripping it out - IsMemoryBacked is always false
    // now so it's all inert, but easy to flip back if disk turns out not to
    // be the fix after all.
    private AudioCaptureSession StartSession(IWaveIn capture, string path, string title)
    {
        return AudioCaptureSession.Start(capture, path, title);
    }

    // How much audio a RAM-backed capture keeps at any given moment -
    // TrimMemoryCaptures below continuously discards anything older than
    // this from the FRONT of the buffer (AudioCaptureSession.TrimTo),
    // keeping exactly one capture per source alive at a roughly constant
    // size instead of growing toward a ceiling before ever rolling to a
    // fresh one. An earlier version of this rolled to a fresh capture once
    // a size ceiling was hit; that let memory climb toward the ceiling
    // before ever shrinking back down (looked like a leak over the first
    // several minutes of any armed session), AND meant a save whose window
    // crossed a roll boundary had to stitch two captures together instead
    // of reading one straight through (a measured save-time slowdown).
    // Trimming avoids both.
    private int RamCaptureRetentionSeconds() =>
        Math.Clamp(_activeConfig?.DurationSeconds ?? 60, 30, 1200) + RamCaptureSlackSeconds;

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

    // Everything about a live microphone capture that cannot be changed
    // without rebuilding it. Rounded to the slider's own resolution so a
    // round-tripped double never reads as a change on its own.
    private static string MicrophoneFilterSignature(ReplayBufferConfig config) =>
        config.MicrophoneNoiseSuppressionEnabled
            ? $"rnnoise@{config.MicrophoneNoiseGateThresholdDb:0.#}dB"
            : "off";

    private void StopStaleAudioCaptures(AudioRoutes routes)
    {
        var wantedChat = routes.ChatRoutes.ToDictionary(route => route.AppName, route => route.ProcessId, StringComparer.OrdinalIgnoreCase);
        var wantedMics = routes.MicrophoneRoutes.ToDictionary(route => route.SourceKey, StringComparer.Ordinal);
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
                // Failed default resolution is transient during a driver/device
                // handover. Keep current capture and retry next notification/poll.
                AudioCaptureKind.Microphone => wantedMics.TryGetValue(capture.SourceKey, out var wantedMic)
                    && (string.IsNullOrEmpty(wantedMic.DeviceId) || string.Equals(capture.DeviceId, wantedMic.DeviceId, StringComparison.Ordinal)),
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
        capture.EndedAtUtc = MonotonicClock.UtcNow;

        // A RAM-backed capture's audio only exists in its MemoryStream - once
        // Dispose() below tears the session down, that data is gone for good
        // unless it's flushed to a real file first. Ended captures are read
        // back via a plain file copy elsewhere (GetOrCreateSourceSnapshot),
        // the same as an already-closed disk-backed capture's WAV, so bridge
        // it to that exact path here instead - an ended memory capture then
        // behaves identically to a disk-backed one for every downstream
        // consumer, no separate code path needed. The resulting file is
        // temporary, cleaned up the same way any other ended capture's file
        // already is (Stop()'s deleteCaptureFiles sweep, PruneOlderThan once
        // the replay window passes it by) - this doesn't reintroduce
        // continuous disk writes, just one final write of whatever's left
        // once a capture actually ends (route change, roll, session stop).
        if (capture.Session.IsMemoryBacked)
        {
            try
            {
                // null: this is persisting the capture itself on stop, not
                // serving one save's window, so it has to keep everything.
                capture.Session.SnapshotTo(capture.Path, earliestNeededUtc: null, out _, AudioSnapshotPurpose.BackgroundArchive);
            }
            catch (Exception error)
            {
                AppLog.Error($"Audio capture memory->file flush on stop failed: {capture.Title}", error);
            }
        }

        try
        {
            capture.Session.Dispose();
        }
        catch
        {
            // Stop best effort.
        }

        AppLog.Debug($"Audio capture stopped: {capture.Title}, pid={capture.ProcessId?.ToString() ?? "none"}, start={capture.StartedAtUtc:o}, end={capture.EndedAtUtc:o}, bytes={capture.Session.BytesWritten}.");
    }

    private void RefreshAudioRoutes()
    {
        if (!_routeRefreshGate.Wait(0)) return;
        try
        {
            var config = _activeConfig;
            if (config is null) return;
            var rolledOver = RollOversizedCaptures();
            TrimMemoryCaptures();
            using var scope = new RouteScope();
            var microphoneRoutes = ResolveMicrophoneRoutes(scope.Enumerator, config.MicrophoneDeviceIds);
            var routes = ResolveAudioRoutes(config, microphoneRoutes, scope);
            var unchanged = string.Equals(routes.RouteKey, _audioRouteKey, StringComparison.Ordinal);
            ApplyRouteInterval(unchanged);
            if (!rolledOver && unchanged) return;
            try
            {
                AppLog.Info("Audio route changed; refreshing replay audio captures.");
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

    // NAudio's WaveFileWriter hard-fails ("WAV file too large") the moment a
    // capture's WAV reaches the 4GiB RIFF cap - at the 96kHz float stereo mix
    // format that's only ~93 minutes - and the throw escaped inside the
    // capture callback, killing the capture thread with no log line. Long
    // sessions then saved clips whose Game/Chat tracks were silent because
    // the captures had quietly been dead for a while. Roll well before the
    // cap: end the capture and let StartAudioCaptures open a fresh file for
    // the same source - the aligned-track builder already stitches multiple
    // captures per source across a save window (same path chat-app restarts
    // use).
    private const long MaxCaptureFileBytes = 3_900_000_000;

    // How far past the configured replay Duration a RAM-backed capture is
    // allowed to grow before rolling - same slack the video ring buffer
    // trims against (NativeReplayBuffer.TrimRingBuffer), so audio and video
    // stay bounded to comparable windows. This is what keeps the in-memory
    // buffer from just growing for as long as the replay buffer stays
    // armed - without it, RAM would climb unboundedly the same way the old
    // disk WAVs did before the 4GiB roll existed for them.
    private const int RamCaptureSlackSeconds = 5;

    private bool RollOversizedCaptures()
    {
        var rolled = false;
        ReplayAudioCapture[] live;
        lock (_lock) live = _audioCaptures.Where(capture => capture.EndedAtUtc is null).ToArray();
        foreach (var capture in live)
        {
            // A capture whose thread died (device loss, an escaped throw) keeps
            // EndedAtUtc null, so HasLiveCapture kept reporting it alive and no
            // replacement ever started - reap it so one does.
            if (capture.Session.Died)
            {
                AppLog.Info($"Audio capture found dead; restarting: {capture.Title}.");
                StopAudioCapture(capture);
                rolled = true;
                continue;
            }

            // RAM-backed (plain replay buffer, not Full Session) captures
            // never hit the 4GiB RIFF cap below - TrimMemoryCaptures keeps
            // them bounded to a much smaller size continuously instead, so
            // there's nothing to roll here for them.
            if (capture.Session.IsMemoryBacked) continue;

            // Written bytes alone miss the quiet-process case: a process
            // capture delivers nothing while its app is silent, so the file
            // stops growing, but the save-time pad-to-now still has to cover
            // that whole gap in silence - size the check on what the file
            // BECOMES after that pad, not what it is now.
            var bytes = capture.Session.BytesWritten;
            if (capture.EffectiveStartedAtUtc is var started && started < MonotonicClock.UtcNow)
            {
                var projectedBytes = (long)((MonotonicClock.UtcNow - started).TotalSeconds * capture.Session.AverageBytesPerSecond);
                if (projectedBytes > bytes) bytes = projectedBytes;
            }

            if (bytes < MaxCaptureFileBytes) continue;
            AppLog.Info($"Audio capture rolled to a new file before the 4GiB WAV cap: {capture.Title}, bytes={bytes}.");
            StopAudioCapture(capture);
            rolled = true;
        }

        return rolled;
    }

    // Keeps every live RAM-backed capture hovering right around
    // RamCaptureRetentionSeconds by continuously discarding older audio from
    // the front of its buffer (AudioCaptureSession.TrimTo) - called on the
    // same 2s cadence as RollOversizedCaptures, alongside it.
    private void TrimMemoryCaptures()
    {
        ReplayAudioCapture[] live;
        lock (_lock) live = _audioCaptures.Where(capture => capture.EndedAtUtc is null && capture.Session.IsMemoryBacked).ToArray();
        if (live.Length == 0) return;

        var retention = TimeSpan.FromSeconds(RamCaptureRetentionSeconds());
        foreach (var capture in live)
        {
            capture.Session.TrimTo(retention);
        }
    }

    // Resolving the audio route is not cheap - see RouteScope for what one pass
    // costs - and it was running unconditionally every 2 seconds for as long as
    // a buffer was armed. Route changes are user actions (launching a game,
    // plugging in a mic), so noticing one a few seconds later is invisible,
    // while the polling itself is CPU a low-end machine is already short of.
    private static readonly TimeSpan FastRouteInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleRouteInterval = TimeSpan.FromSeconds(5);
    // Consecutive unchanged passes before backing off. At the fast interval
    // that is ~10s of close attention after anything moves, which covers a
    // game still spawning its audio helper processes.
    private const int StableRoutePassesBeforeBackoff = 5;

    private void StartAudioRouteTimer()
    {
        _audioRouteTimer?.Dispose();
        _stableRoutePasses = 0;
        _routeInterval = FastRouteInterval;
        _audioRouteTimer = new Timer(_ => RefreshAudioRoutes(), null, FastRouteInterval, FastRouteInterval);
    }

    private void ApplyRouteInterval(bool unchanged)
    {
        if (!unchanged)
        {
            _stableRoutePasses = 0;
            SetRouteInterval(FastRouteInterval);
            return;
        }

        if (_stableRoutePasses < StableRoutePassesBeforeBackoff) _stableRoutePasses++;
        if (_stableRoutePasses >= StableRoutePassesBeforeBackoff) SetRouteInterval(IdleRouteInterval);
    }

    private void SetRouteInterval(TimeSpan interval)
    {
        if (_routeInterval == interval) return;
        _routeInterval = interval;
        try { _audioRouteTimer?.Change(interval, interval); }
        catch (ObjectDisposedException) { }
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
                return DefaultMicrophone.Get(enumerator).ID;
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

    private static MicrophoneRoute[] ResolveMicrophoneRoutes(MMDeviceEnumerator enumerator, IReadOnlyList<string>? configuredIds)
    {
        var configured = NormalizedList(configuredIds);
        var resolved = new List<MicrophoneRoute>();
        foreach (var id in configured)
        {
            var resolvedId = ResolveMicrophoneDeviceId(enumerator, id);
            var sourceKey = string.IsNullOrWhiteSpace(id) || id == AudioDeviceOption.DefaultDeviceId
                ? AudioDeviceOption.DefaultDeviceId
                : id;
            resolved.Add(new MicrophoneRoute(sourceKey, resolvedId));
        }

        return resolved.DistinctBy(route => route.SourceKey, StringComparer.Ordinal).ToArray();
    }

    private static List<string> NormalizedList(IReadOnlyList<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AudioRoutes ResolveAudioRoutes(ReplayBufferConfig config, MicrophoneRoute[] microphoneRoutes, RouteScope scope)
    {
        var chatAppNames = AudioProcessIdentity.NormalizeList(config.ChatAudioProcessNames);
        foreach (var appName in AudioProcessIdentity.NormalizeDictionary(config.AdditionalAudioProcesses).Keys)
        {
            if (!chatAppNames.Contains(appName, StringComparer.OrdinalIgnoreCase)) chatAppNames.Add(appName);
        }

        // Multi-process apps (Discord, browsers, etc.) share one executable name across
        // several OS processes. StartProcessLoopbackCapture already uses
        // IncludeTargetProcessTree, so capturing more than one PID from the same app
        // captures its audio twice - the reported "chat audio doubled" bug. One PID
        // per app; ResolveAppRootProcessId picks which (the real tree root, verified
        // via parent PIDs - the old "lowest PID" guess broke whenever PID recycling
        // handed a detached helper a lower number than the actual root: the capture
        // then attached to a tree that never renders audio and recorded pure silence
        // while voice played in the real tree).
        var chatRoutes = new List<ChatRoute>();
        foreach (var appName in chatAppNames)
        {
            var rootPid = ResolveAppRootProcessId(appName, scope);
            if (rootPid > 0) chatRoutes.Add(new ChatRoute(appName, rootPid));
        }

        var normalizedExclusions = AudioProcessIdentity.NormalizeList(config.GameAudioExcludedProcesses);
        var useProcessRouting = chatRoutes.Count > 0 || normalizedExclusions.Count > 0;
        var exclusionNames = normalizedExclusions
            .Concat(chatAppNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedPids = exclusionNames
            .SelectMany(scope.ProcessIds)
            .Distinct()
            .OrderBy(pid => pid)
            .ToArray();
        var selfPid = Environment.ProcessId;
        var gamePids = useProcessRouting ? ResolveGameAudioProcessIds(config.GameExecutableName ?? string.Empty, excludedPids, selfPid, scope) : Array.Empty<int>();
        // One capture per distinct process tree - each capture uses
        // IncludeTargetProcessTree, so keeping a pid whose ancestor is also in
        // the set records the same audio twice (or more: a real save showed
        // FOUR live Game captures of overlapping Fortnite trees, mixed into
        // one smeared/echoing Game track). Same bug class as the chat lowest-
        // PID fix, on the game side.
        var rawGamePids = gamePids;
        gamePids = CollapseToTreeRoots(gamePids, scope);
        if (gamePids.Length != rawGamePids.Length)
        {
            AppLog.Info($"Game audio pids collapsed to tree roots: raw={FormatIds(rawGamePids)}, roots={FormatIds(gamePids)}.");
        }
        // Microphone filtering is part of the key even though it names no
        // device: it is applied live, inside the capture, so changing it has to
        // make the refresh timer see the route as changed and rebuild. Without
        // this the toggle would appear to do nothing until the next launch.
        var key = $"{useProcessRouting}|{string.Join(',', chatRoutes.OrderBy(route => route.AppName, StringComparer.OrdinalIgnoreCase).Select(route => $"{route.AppName}:{route.ProcessId}"))}|{string.Join(',', excludedPids)}|{string.Join(',', gamePids)}|{string.Join(',', microphoneRoutes.OrderBy(route => route.SourceKey, StringComparer.Ordinal).Select(route => $"{route.SourceKey}:{route.DeviceId}"))}|{MicrophoneFilterSignature(config)}";
        return new AudioRoutes(chatRoutes.ToArray(), excludedPids, gamePids, useProcessRouting, key, microphoneRoutes);
    }

    private static string FormatIds(IEnumerable<int> ids)
    {
        var values = ids.ToArray();
        return values.Length == 0 ? "none" : string.Join(",", values);
    }

    // Picks the PID whose process tree actually contains the app's audio -
    // builds the real parent/child relationships from a Toolhelp snapshot
    // instead of assuming "lowest PID = root". Roots are the app's processes
    // whose parent isn't another process of the same app; when several exist
    // (crash handlers, detached helpers), prefer the root whose subtree holds
    // a currently-active audio session, then the one with the most children.
    private static int ResolveAppRootProcessId(string appName, RouteScope scope)
    {
        var appPids = scope.ProcessIds(appName).ToHashSet();
        if (appPids.Count == 0) return 0;
        if (appPids.Count == 1) return appPids.First();

        try
        {
            var parents = scope.ParentProcessIds;
            var roots = appPids.Where(pid => !parents.TryGetValue(pid, out var parent) || !appPids.Contains(parent)).ToArray();
            if (roots.Length == 0) return appPids.Min();
            if (roots.Length == 1) return roots[0];

            int SubtreeOf(int pid)
            {
                var current = pid;
                // Walk up within the app's own pids to this pid's root.
                for (var hops = 0; hops < 32 && parents.TryGetValue(current, out var parent) && appPids.Contains(parent); hops++)
                {
                    current = parent;
                }
                return current;
            }

            var audioActive = scope.ActiveAudioProcessIds.Where(appPids.Contains).ToArray();
            var audioRoot = audioActive.Select(SubtreeOf).FirstOrDefault(roots.Contains);
            if (audioRoot > 0)
            {
                AppLog.Info($"Chat route: {appName} root {audioRoot} chosen via active audio session (roots: {string.Join(",", roots)}).");
                return audioRoot;
            }

            var byTreeSize = roots
                .OrderByDescending(root => appPids.Count(pid => SubtreeOf(pid) == root))
                .First();
            AppLog.Info($"Chat route: {appName} root {byTreeSize} chosen via largest subtree (roots: {string.Join(",", roots)}).");
            return byTreeSize;
        }
        catch (Exception error)
        {
            AppLog.Error($"Chat route root resolution failed for {appName}; falling back to lowest PID.", error);
            return appPids.Min();
        }
    }

    // Drops every pid that has an ancestor also present in the set - the
    // survivors are the roots of distinct process trees.
    private static int[] CollapseToTreeRoots(IReadOnlyCollection<int> pids, RouteScope scope)
    {
        if (pids.Count <= 1) return pids.ToArray();
        try
        {
            var set = pids.ToHashSet();
            var parents = scope.ParentProcessIds;
            return pids.Where(pid =>
            {
                var current = pid;
                for (var hops = 0; hops < 32 && parents.TryGetValue(current, out var parent) && parent != 0 && parent != current; hops++)
                {
                    if (set.Contains(parent)) return false;
                    current = parent;
                }
                return true;
            }).OrderBy(pid => pid).ToArray();
        }
        catch (Exception error)
        {
            AppLog.Error("Collapsing audio pids to tree roots failed; keeping the full set.", error);
            return pids.ToArray();
        }
    }

    private static Dictionary<int, int> SnapshotParentProcessIds()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return parents;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return parents;
            do
            {
                parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return parents;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

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
    private static int[] ResolveGameAudioProcessIds(string gameExecutableName, int[] excludedPids, int selfPid, RouteScope scope)
    {
        var activeAudioPids = scope.ActiveAudioProcessIds
            .Where(pid => pid != selfPid)
            .Except(excludedPids)
            .ToHashSet();

        var gameNamePids = scope.ProcessIds(gameExecutableName).ToHashSet();
        var matched = activeAudioPids.Where(gameNamePids.Contains).OrderBy(pid => pid).ToArray();
        return matched.Length > 0 ? matched : activeAudioPids.OrderBy(pid => pid).ToArray();
    }

    private static int[] ResolveActiveAudioProcessIds(MMDeviceEnumerator enumerator)
    {
        var ids = new HashSet<int>();
        try
        {
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

        return ids.ToArray();
    }

    public static IReadOnlyList<string> GetActiveAudioProcessNames()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        using var enumerator = new MMDeviceEnumerator();
        return ResolveActiveAudioProcessIds(enumerator)
            .Select(processId =>
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    return process.ProcessName;
                }
                catch { return string.Empty; }
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ActiveAudioProcess> GetActiveAudioProcesses()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ActiveAudioProcess>();
        using var enumerator = new MMDeviceEnumerator();
        return ResolveActiveAudioProcessIds(enumerator)
            .Select(processId =>
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    return new ActiveAudioProcess(process.ProcessName, processId, process.MainModule?.FileName ?? string.Empty);
                }
                catch { return null; }
            })
            .Where(process => process is not null && !string.IsNullOrWhiteSpace(process.Name))
            .Select(process => process!)
            .GroupBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(process => process.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    // One pass of audio-route resolution, memoising the expensive lookups that
    // several steps of that pass each used to do for themselves.
    //
    // Per tick this was: three SnapshotParentProcessIds calls (each a Toolhelp
    // snapshot of every process on the machine), a Process.GetProcessesByName
    // per configured name plus another for the game (each enumerates every
    // process and allocates a Process object for it), and two MMDeviceEnumerator
    // constructions with an audio-session walk behind one of them. None of it
    // can change within a single pass, and on a 2-second timer with a game
    // running it was real CPU on a machine already short of it.
    private sealed class RouteScope : IDisposable
    {
        private readonly Dictionary<string, int[]> _processIdsByName = new(StringComparer.OrdinalIgnoreCase);
        private MMDeviceEnumerator? _enumerator;
        private Dictionary<int, int>? _parentProcessIds;
        private int[]? _activeAudioProcessIds;

        public MMDeviceEnumerator Enumerator => _enumerator ??= new MMDeviceEnumerator();
        public Dictionary<int, int> ParentProcessIds => _parentProcessIds ??= SnapshotParentProcessIds();
        public int[] ActiveAudioProcessIds => _activeAudioProcessIds ??= ResolveActiveAudioProcessIds(Enumerator);

        public int[] ProcessIds(string processName)
        {
            var key = Path.GetFileNameWithoutExtension(processName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(key)) return Array.Empty<int>();
            if (_processIdsByName.TryGetValue(key, out var cached)) return cached;
            var resolved = ResolveProcessIds(key).ToArray();
            _processIdsByName[key] = resolved;
            return resolved;
        }

        public void Dispose() => _enumerator?.Dispose();
    }

    // A snapshot of a capture's WAV plus WavStartUtc: the wall-clock moment
    // WAV position 0 actually corresponds to, anchored from the END of the
    // data (last sample = the snapshot/capture-end moment) rather than from
    // FirstSampleUtc. Start-anchoring let capture-clock drift accumulate for
    // the capture's whole lifetime: real device clocks aren't exactly their
    // nominal rate, so N seconds of wall-clock never yields exactly N seconds
    // of samples, and after an hour-plus of one continuous capture the
    // window's WAV offset could be seconds off - clips saved late in a long
    // session came out audibly desynced while early ones were fine. The
    // Legacy backend never showed this only because it restarted captures
    // every ~20s segment. End-anchoring makes the error zero at the newest
    // sample and only ~a window's worth of drift (micro-seconds over 60s)
    // within a clip.
    private sealed record SourceSnapshot(string Path, DateTime WavStartUtc);

    // One full copy of a capture's WAV per save operation, shared by every
    // segment window - this used to happen inside SnapshotAudioFile, once per
    // 60s chunk, which for a multi-hour Full Session finalize meant copying
    // the same multi-GB source WAV hundreds of times (per track!). That
    // quadratic disk churn is what made the whole PC crawl during session
    // muxing. The single snapshot is taken up front, so every chunk window
    // (all of which end at-or-before the moment the save started) is fully
    // covered by it.
    // Wrapped in Lazy<Task<...>> by the caller (sourceSnapshotCache) so that,
    // even with multiple segments/tracks racing to snapshot the same capture
    // concurrently, the (potentially multi-GB) source copy below only ever
    // runs once per capture per save.
    private async Task<SourceSnapshot?> CreateSourceSnapshotAsync(ReplayAudioCapture capture, ICollection<string> snapshots, DateTime? earliestNeededUtc, AudioSnapshotPurpose snapshotPurpose)
    {
        // One snapshot copy at a time across the whole save. These are pure
        // sequential disk reads and writes: running the Game/Chat/Mic copies
        // concurrently gains nothing on one disk and simply multiplies the
        // stall, which is how saving mid-game came to freeze the game and the
        // pointer. The Lazy cache already dedupes per capture; this bounds
        // what is left.
        await SourceSnapshotGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
            var sourceSnapshotPath = Path.Combine(_bufferFolder, $"audio_source_{Guid.NewGuid():N}.wav");
            // For a live capture the newest sample in the snapshot corresponds to
            // the pad-to-now moment SnapshotTo stamps (NOT "now" after the copy -
            // the copy itself still takes a moment); for an ended one it was
            // written at EndedAtUtc. Monotonic - a system clock step between
            // capture start and save would otherwise shift the anchor by the
            // step amount.
            var lastSampleUtc = capture.EndedAtUtc ?? default;
            var copyTimer = System.Diagnostics.Stopwatch.StartNew();
            var copied = capture.EndedAtUtc is null
                ? capture.Session.SnapshotTo(sourceSnapshotPath, earliestNeededUtc, out lastSampleUtc, snapshotPurpose)
                : CopyAudioFile(capture.Path, sourceSnapshotPath);
            var copyMs = copyTimer.ElapsedMilliseconds;
            if (!copied || !IsUsableAudioFile(sourceSnapshotPath))
            {
                TryDelete(sourceSnapshotPath);
                return (SourceSnapshot?)null;
            }

            lock (snapshots) snapshots.Add(sourceSnapshotPath);

            var wavStartUtc = capture.EffectiveStartedAtUtc;
            try
            {
                using var reader = new WaveFileReader(sourceSnapshotPath);
                wavStartUtc = lastSampleUtc - reader.TotalTime;
                var driftMs = (wavStartUtc - capture.EffectiveStartedAtUtc).TotalMilliseconds;
                AppLog.Debug($"Audio snapshot anchored: kind={capture.Kind}, sourceKey={capture.SourceKey}, wavSeconds={reader.TotalTime.TotalSeconds:0.0}, startDriftMs={driftMs:0}, copyMs={copyMs}, copyBytes={new FileInfo(sourceSnapshotPath).Length}.");
            }
            catch (Exception error)
            {
                // Fall back to the old start-anchored mapping rather than failing
                // the save outright.
                AppLog.Error($"Audio snapshot duration read failed for {sourceSnapshotPath}; falling back to start-anchored alignment.", error);
            }

            return new SourceSnapshot(sourceSnapshotPath, wavStartUtc);
            });
        }
        finally
        {
            SourceSnapshotGate.Release();
        }
    }

    private Task<SourceSnapshot?> GetOrCreateSourceSnapshotAsync(ReplayAudioCapture capture, ICollection<string> snapshots, ConcurrentDictionary<ReplayAudioCapture, Lazy<Task<SourceSnapshot?>>> sourceSnapshotCache, DateTime? earliestNeededUtc, AudioSnapshotPurpose snapshotPurpose)
    {
        return sourceSnapshotCache.GetOrAdd(capture, c => new Lazy<Task<SourceSnapshot?>>(
            () => CreateSourceSnapshotAsync(c, snapshots, earliestNeededUtc, snapshotPurpose),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private async Task<string> SnapshotAudioFileAsync(ReplayAudioCapture? capture, DateTime windowStartUtc, double durationSeconds, ICollection<string> snapshots, ConcurrentDictionary<ReplayAudioCapture, Lazy<Task<SourceSnapshot?>>> sourceSnapshotCache, DateTime? earliestNeededUtc, AudioSnapshotPurpose snapshotPurpose, ReplayBufferConfig? config = null, int channels = 2)
    {
        // BytesWritten, not IsUsableAudioFile(capture.Path) - a RAM-backed
        // capture (the plain replay-buffer window) never has a real file at
        // capture.Path at all, so that check always failed for it. BytesWritten
        // reports actual captured content either way, disk or memory.
        if (capture is null || capture.Session.BytesWritten == 0) return string.Empty;

        var snapshotPath = Path.Combine(_bufferFolder, $"audio_{Guid.NewGuid():N}.wav");
        try
        {
            var sourceSnapshot = await GetOrCreateSourceSnapshotAsync(capture, snapshots, sourceSnapshotCache, earliestNeededUtc, snapshotPurpose);
            if (sourceSnapshot is null) return string.Empty;
            var sourceSnapshotPath = sourceSnapshot.Path;

            var captureEndUtc = capture.EndedAtUtc ?? MonotonicClock.UtcNow;
            var windowEndUtc = windowStartUtc + TimeSpan.FromSeconds(durationSeconds);
            // End-anchored (see SourceSnapshot) - the wall-clock moment WAV
            // position 0 maps to, NOT FirstSampleUtc.
            var effectiveStartUtc = sourceSnapshot.WavStartUtc;
            var overlapStartUtc = effectiveStartUtc > windowStartUtc ? effectiveStartUtc : windowStartUtc;
            var overlapEndUtc = captureEndUtc < windowEndUtc ? captureEndUtc : windowEndUtc;
            if (overlapEndUtc <= overlapStartUtc) return string.Empty;

            var trimStart = Math.Max(0, (overlapStartUtc - effectiveStartUtc).TotalSeconds);
            var overlapDuration = Math.Max(0, (overlapEndUtc - overlapStartUtc).TotalSeconds);
            var delayMs = Math.Max(0, (int)Math.Round((overlapStartUtc - windowStartUtc).TotalMilliseconds));
            // -ss/-t as INPUT options: WAV is constant-bitrate, so ffmpeg seeks
            // straight to the byte offset and reads only this window's worth of
            // the (potentially hours-long) source snapshot, instead of the old
            // atrim filter approach that decoded the file from the top every
            // chunk.
            var filters = $"[0:a]asetpts=PTS-STARTPTS,aresample=48000,adelay={delayMs}|{delayMs},apad=whole_dur={FormatSeconds(durationSeconds)},atrim=0:{FormatSeconds(durationSeconds)}[out]";
            AppLog.Debug($"Replay audio overlap: kind={capture.Kind}, pid={capture.ProcessId?.ToString() ?? "none"}, trim={trimStart:0.###}s, overlap={overlapDuration:0.###}s, delay={delayMs}ms, bytes={capture.Session.BytesWritten}.");

            var result = await RunGatedProcessAsync("ffmpeg", new[]
            {
                "-y",
                "-v", "error",
                "-ss", FormatSeconds(trimStart),
                "-t", FormatSeconds(overlapDuration),
                "-i", sourceSnapshotPath,
                "-filter_complex", filters,
                "-map", "[out]",
                // Track-dependent - the microphone track is mono unless the
                // user says their mic really is stereo. See
                // TrackChannelCount.
                "-ac", channels.ToString(CultureInfo.InvariantCulture),
                // f32, not s16, for every intermediate in the save chain (this,
                // the silent filler, and the mix - concat is -c copy, so all
                // three have to agree). Captures arrive as 32-bit float and a
                // track can sit far below full scale: the Game track of a
                // measured clip peaked at -34dBFS, which through s16 left it
                // with 10 significant bits before it ever reached the AAC
                // encoder. Whatever the user turns up to hear, they should not
                // be turning up quantization steps this pipeline added.
                "-c:a", "pcm_f32le",
                snapshotPath
            }, CancellationToken.None);
            if (result.ExitCode != 0 || !IsUsableAudioFile(snapshotPath))
            {
                TryDelete(snapshotPath);
                return string.Empty;
            }

            lock (snapshots) snapshots.Add(snapshotPath);
            return snapshotPath;
        }
        catch
        {
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
        ConcurrentDictionary<ReplayAudioCapture, Lazy<Task<SourceSnapshot?>>> sourceSnapshotCache,
        DateTime? earliestNeededUtc,
        AudioSnapshotPurpose snapshotPurpose,
        CancellationToken cancellationToken,
        ReplayBufferConfig? config = null,
        int volumePercent = 100)
    {
        var channels = TrackChannelCount(kind, config);
        // Segments are independent time windows, so they overlap rather than
        // running one ffmpeg spawn after another - but only as far as
        // FfmpegGate allows. Unbounded, a Full Session's hours of 60s segments
        // would each take a process at once. GetOrCreateSourceSnapshotAsync
        // still dedupes the per-capture source copy across whichever segments
        // race into needing it first.
        var segmentTasks = segmentWindows.Select(async window =>
        {
            var (startUtc, durationSeconds) = window;
            var endUtc = startUtc + TimeSpan.FromSeconds(durationSeconds);
            var overlapping = captures
                .Where(capture => capture.Kind == kind
                    && (sourceKey is null || AudioProcessIdentity.Equals(capture.SourceKey, sourceKey))
                    && AudioCaptureOverlaps(capture, startUtc, endUtc))
                .ToArray();
            if (!allowMix && overlapping.Length > 1)
            {
                overlapping = new[] { overlapping[^1] };
            }

            var clipPathResults = await Task.WhenAll(overlapping
                .Select(capture => SnapshotAudioFileAsync(capture, startUtc, durationSeconds, snapshots, sourceSnapshotCache, earliestNeededUtc, snapshotPurpose, config, channels)));
            var clipPaths = clipPathResults
                .Where(IsUsableAudioFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (clipPaths.Length == 0)
            {
                return await CreateSilentClipAsync(durationSeconds, snapshots, cancellationToken, channels);
            }
            if (clipPaths.Length == 1)
            {
                return clipPaths[0];
            }

            return await MixClipsAsync(clipPaths, durationSeconds, snapshots, cancellationToken);
        }).ToArray();

        var segmentClips = await Task.WhenAll(segmentTasks);

        if (segmentClips.Length == 0)
        {
            var totalDuration = segmentWindows.Sum(window => window.DurationSeconds);
            return await CreateSilentClipAsync(totalDuration, snapshots, cancellationToken, channels);
        }

        var outputPath = segmentClips.Length == 1
            ? segmentClips[0]
            : await ConcatClipsAsync(segmentClips, snapshots, cancellationToken);
        return volumePercent == 100
            ? outputPath
            : await ApplyGainAsync(outputPath, volumePercent, snapshots, cancellationToken);
    }

    // Only the microphone is ever mono. Game and app tracks are loopback
    // captures of real stereo mixes, so folding them down would throw away
    // actual stereo content.
    private static int TrackChannelCount(AudioCaptureKind kind, ReplayBufferConfig? config)
    {
        if (kind != AudioCaptureKind.Microphone) return 2;
        return string.Equals(config?.MicrophoneChannelMode, "Stereo", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    // -120 dBFS = amplitude 0.000001. The intermediate WAVs are float PCM,
    // but accept other PCM WAVs too so a future encoder change cannot turn an
    // unreadable file into a falsely omitted Spotify lane. Fail open on any
    // read problem: keeping a silent lane is safer than losing real audio.
    internal static bool IsSilentWaveFile(string path)
    {
        const float silentPeak = 0.000001f;
        try
        {
            using var reader = new WaveFileReader(path);
            var buffer = new byte[64 * 1024];
            int bytesRead;
            while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (!AudioSampleFormat.TryGetPeak(reader.WaveFormat, buffer, bytesRead, out var peak)) return false;
                if (peak > silentPeak) return false;
            }

            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"Spotify silence check failed for {path}; keeping track.", error);
            return false;
        }
    }

    private async Task<string> ApplyGainAsync(string inputPath, int volumePercent, ICollection<string> snapshots, CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(_bufferFolder, $"audio_gain_{Guid.NewGuid():N}.wav");
        var gain = (Math.Clamp(volumePercent, 0, 150) / 100d).ToString("0.###", CultureInfo.InvariantCulture);
        var result = await RunGatedProcessAsync("ffmpeg", new[] { "-y", "-v", "error", "-i", inputPath, "-af", $"volume={gain}", "-c:a", "pcm_f32le", outputPath }, cancellationToken);
        if (result.ExitCode != 0 || !IsUsableAudioFile(outputPath))
        {
            AppLog.Error($"App audio gain failed: volume={volumePercent}, error={result.Error}");
            TryDelete(outputPath);
            return inputPath;
        }

        lock (snapshots) snapshots.Add(outputPath);
        return outputPath;
    }

    // channels has to match whatever the real segments of this track came out
    // as: ConcatClipsAsync stitches silence and content together with -c copy,
    // which cannot join a mono clip to a stereo one.
    private async Task<string> CreateSilentClipAsync(double durationSeconds, ICollection<string> snapshots, CancellationToken cancellationToken, int channels = 2)
    {
        var path = Path.Combine(_bufferFolder, $"audio_silent_{Guid.NewGuid():N}.wav");
        var layout = channels == 1 ? "mono" : "stereo";
        var result = await RunGatedProcessAsync("ffmpeg", new[]
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-t", FormatSeconds(durationSeconds), "-i", $"anullsrc=channel_layout={layout}:sample_rate=48000",
            "-c:a", "pcm_f32le",
            path
        }, cancellationToken);
        if (result.ExitCode != 0 || !IsUsableAudioFile(path))
        {
            AppLog.Error($"Silent clip generation failed: duration={durationSeconds:0.###}s, error={result.Error}");
        }

        lock (snapshots) snapshots.Add(path);
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
            "-c:a", "pcm_f32le",
            path
        });
        var result = await RunGatedProcessAsync("ffmpeg", args, cancellationToken);
        if (result.ExitCode != 0 || !IsUsableAudioFile(path))
        {
            AppLog.Error($"Audio mix failed: clips={clipPaths.Count}, error={result.Error}");
            return await CreateSilentClipAsync(durationSeconds, snapshots, cancellationToken);
        }

        lock (snapshots) snapshots.Add(path);
        return path;
    }

    private async Task<string> ConcatClipsAsync(IReadOnlyList<string> clipPaths, ICollection<string> snapshots, CancellationToken cancellationToken)
    {
        var concatPath = Path.Combine(_bufferFolder, $"audio_concat_{Guid.NewGuid():N}.txt");
        var outputPath = Path.Combine(_bufferFolder, $"audio_concat_{Guid.NewGuid():N}.wav");
        await File.WriteAllLinesAsync(concatPath, clipPaths.Select(path => $"file '{EscapeConcatPath(path)}'"), cancellationToken);
        try
        {
            var result = await RunGatedProcessAsync("ffmpeg", new[] { "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", concatPath, "-c", "copy", outputPath }, cancellationToken);
            if (result.ExitCode != 0 || !IsUsableAudioFile(outputPath))
            {
                AppLog.Error($"Audio concat failed: clips={clipPaths.Count}, error={result.Error}");
            }
        }
        finally
        {
            TryDelete(concatPath);
        }

        lock (snapshots) snapshots.Add(outputPath);
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

    // Every ffmpeg spawn inside a save goes through here so FfmpegGate bounds
    // how many run at once. The final mux in NativeReplayBuffer deliberately
    // calls RunProcessAsync directly - it is a single process that runs after
    // all of this has finished, so gating it would only add a wait.
    private static async Task<(int ExitCode, string Output, string Error)> RunGatedProcessAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        await FfmpegGate.WaitAsync(cancellationToken);
        try
        {
            return await RunProcessAsync(fileName, args, cancellationToken);
        }
        finally
        {
            FfmpegGate.Release();
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
        var info = new ProcessStartInfo(FfmpegPathResolver.Resolve(fileName))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = FfmpegPathResolver.WorkingDirectory,
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
        var captureEndUtc = capture.EndedAtUtc ?? MonotonicClock.UtcNow;
        return capture.EffectiveStartedAtUtc < windowEndUtc && captureEndUtc > windowStartUtc;
    }

    public sealed record ChatRoute(string AppName, int ProcessId);

    internal sealed record MicrophoneRoute(string SourceKey, string DeviceId);

    internal sealed record AudioRoutes(ChatRoute[] ChatRoutes, int[] ExcludedProcessIds, int[] GameProcessIds, bool UseProcessRouting, string RouteKey, MicrophoneRoute[] MicrophoneRoutes);

    public enum AudioCaptureKind
    {
        Game,
        Chat,
        Microphone
    }

    internal sealed class ReplayAudioCapture
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
