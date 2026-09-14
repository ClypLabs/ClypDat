using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClypDat.Capture.Abstractions;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

internal interface ILiveFullSessionControl
{
    Task SetFullSessionEnabledAsync(bool enabled, CancellationToken token);
}

internal sealed class LinuxReplayBuffer : IReplayBuffer, IReplayCaptureDiagnostics, IReplayBackendReadiness,
    ILiveFullSessionControl, IFullSessionFinalizeReporter
{
    private readonly Func<ReplayBufferConfig>? _configProvider;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly SemaphoreSlim _jobs = new(2, 2);
    private readonly object _gate = new();
    private readonly HashSet<Task> _finalizes = [];
    private readonly Dictionary<string, FullSessionFinalizeProgress> _progress = new();
    private readonly SemaphoreSlim _sessionFinalizers = new(1, 1);
    internal IReadOnlyList<FullSessionFinalizeProgress> ActiveFinalizes { get { lock (_gate) return _progress.Values.ToArray(); } }
    private void ReportProgress(string path, FullSessionFinalizeProgress? progress) {
        FullSessionFinalizeProgress[] snapshot;
        lock (_gate) { if (progress is null) _progress.Remove(path); else _progress[path] = progress; snapshot = _progress.Values.ToArray(); }
        FullSessionFinalizeChanged?.Invoke(this, snapshot);
    }
    private Process? _recorder;
    private Task? _observer;
    private CancellationTokenSource? _lifetime;
    private ReplayBufferConfig? _config;
    private TaskCompletionSource<double>? _ready;
    private string? _socketPath, _runtimeFolder, _staging;
    private long _requestId;
    private double _monotonicStart;
    private bool _session, _stopping;
    private volatile bool _recording;
    private ReplayCaptureHealth _health = ReplayCaptureHealth.Unknown("KDE / GSR");
    private ReplayBackendReadiness _readiness = new(false, ReplayBackendCapabilities.None, "Waiting for private KDE recorder readiness.");
    internal LinuxReplayBuffer(Func<ReplayBufferConfig>? configProvider = null) => _configProvider = configProvider;
    public bool IsRecording => _recording;
    public TimeSpan Duration => TimeSpan.FromSeconds(_recording ? Math.Min(_config!.DurationSeconds,
        Math.Max(0, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - _monotonicStart)) : 0);
    public event EventHandler? RecordingStopped;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;
    public event EventHandler<IReadOnlyList<FullSessionFinalizeProgress>>? FullSessionFinalizeChanged;
    public ReplayBackendReadiness GetReadiness() => _readiness;
    public ReplayCaptureHealth GetHealthSnapshot() => _health;
    private static string RecorderPath => Path.Combine(AppContext.BaseDirectory, "libexec", "clypdat-gsr");
    private void Publish(ReplayCaptureState state, string reason = "")
    {
        _health = _health with { State = state, LastFailure = reason, UpdatedUtc = DateTime.UtcNow,
            TargetFrameRate = _config?.FrameRate ?? 0, CaptureMode = _config?.LinuxTarget?.Kind.ToString() ?? "KDE",
            StartupPhase = state == ReplayCaptureState.Healthy ? ReplayCaptureStartupPhase.Ready : ReplayCaptureStartupPhase.None };
        HealthChanged?.Invoke(this, _health);
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_recording) return;
            if (_recorder is not null) await TerminateAsync();
            _config = _configProvider?.Invoke() ?? throw new PlatformNotSupportedException("No Linux capture target configured.");
            var source = _config.LinuxTarget?.ToRecorderSource() ?? throw new InvalidOperationException("Selected KDE source unavailable.");
            if (_config.MicrophoneNoiseSuppressionEnabled || _config.MicrophoneNoiseGateThresholdDb > -100)
                throw new PlatformNotSupportedException("Microphone noise processing is not available on Linux. Disable it in audio settings.");
            if (!string.IsNullOrEmpty(_config.ChatAudioDeviceId))
                throw new PlatformNotSupportedException("Device-only chat isolation is not available on Linux. Select a chat application.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var capabilities = JsonDocument.Parse(await LinuxMediaProcess.RunAsync(RecorderPath, ["--clypdat-capabilities"], timeout.Token));
            var c = capabilities.RootElement;
            if (c.GetProperty("version").GetInt32() != 2 || !c.GetProperty("kdeCapture").GetBoolean() || !c.GetProperty("exactTimeline").GetBoolean())
                throw new InvalidOperationException("Private recorder version mismatch. Republish this Linux build.");
            _runtimeFolder = Path.Combine(Path.GetTempPath(), "clypdat-gsr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_runtimeFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _socketPath = Path.Combine(_runtimeFolder, "control.sock");
            _staging = Path.Combine(AppDataPaths.Root, "replay-staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_staging);
            _lifetime = new(); _stopping = false;
            _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _recorder = new Process { StartInfo = new(RecorderPath) { UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var argument in BuildArguments(_config, source, _socketPath, _staging)) _recorder.StartInfo.ArgumentList.Add(argument);
            _recorder.StartInfo.Environment.Remove("DISPLAY");
            _recorder.Start();
            Publish(ReplayCaptureState.Starting);
            _observer = ObserveAsync(_recorder, _lifetime.Token);
            _monotonicStart = await _ready.Task.WaitAsync(timeout.Token);
            if (_recorder.HasExited || _observer.IsCompleted) throw new IOException("Private recorder exited during readiness.");
            _recording = true;
            _readiness = new(true, ReplayBackendCapabilities.IsolatedWindow | ReplayBackendCapabilities.Monitor |
                ReplayBackendCapabilities.RamReplay | ReplayBackendCapabilities.FocusFreeze | ReplayBackendCapabilities.ExactClipWindows |
                ReplayBackendCapabilities.FullSession, "Experimental KDE capture ready.");
            Publish(ReplayCaptureState.Healthy);
            if (_config.FullSessionRecordingEnabled) await SetFullSessionEnabledAsync(true, timeout.Token);
        }
        catch (Exception error)
        {
            _recording = false;
            _readiness = new(false, ReplayBackendCapabilities.None, error.Message);
            Publish(ReplayCaptureState.Failed, error.Message);
            await TerminateAsync();
            throw;
        }
        finally { _lifecycle.Release(); }
    }
    internal static IReadOnlyList<string> BuildArguments(ReplayBufferConfig config, string source, string socket, string staging)
    {
        if (config.FrameRate <= 0 || config.DurationSeconds <= 0 || config.BitrateMbps <= 0) throw new ArgumentException("Invalid replay settings.");
        List<string> args = ["-w", source, "-f", config.FrameRate.ToString(CultureInfo.InvariantCulture), "-r",
            (config.DurationSeconds + 4).ToString(CultureInfo.InvariantCulture), "-replay-storage", "ram",
            "-restart-replay-on-save", "no", "-c", "mkv", "-o", staging, "-ro", staging, "-ipc", socket,
            "-k", RecorderCodec(config.VideoCodec), "-encoder", config.EncoderMode.Equals("CPU", StringComparison.OrdinalIgnoreCase) ? "cpu" : "gpu",
            "-bm", "cbr", "-q", (config.BitrateMbps * 1000).ToString(CultureInfo.InvariantCulture),
            "-fm", config.FrameRateMode.Equals("VFR", StringComparison.OrdinalIgnoreCase) ? "vfr" : "cfr",
            "-cursor", config.CaptureCursor ? "yes" : "no"];
        if (config.MaxHeight > 0) args.AddRange(["-s", $"0x{config.MaxHeight}"]);
        foreach (var track in LinuxAudioTrackPlan.Create(config)) args.AddRange(["-a", track.RecorderInput]);
        return args;
    }
    internal static string RecorderCodec(string codec) => codec switch { "H.264" => "h264", "H.265" or "HEVC" => "hevc", "AV1" => "av1", _ => throw new NotSupportedException($"Unsupported Linux codec: {codec}") };
    internal static bool IsRecorderFailure(string line) =>
        !line.StartsWith("gsr info:", StringComparison.OrdinalIgnoreCase) &&
        (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase));
    private async Task ObserveAsync(Process process, CancellationToken token)
    {
        try
        {
            var lastFailure = string.Empty;
            var errors = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(token) is { } line) {
                    AppLog.Debug("Private recorder: " + line);
                    if (IsRecorderFailure(line))
                        lastFailure = line.Length > 1000 ? line[..1000] : line;
                }
            }, token);
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                if (!line.StartsWith('{')) continue;
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("event", out var kind) && kind.GetString() == "ready")
                    _ready?.TrySetResult(json.RootElement.GetProperty("monotonicStart").GetDouble());
                if (json.RootElement.TryGetProperty("event", out kind) && kind.GetString() == "health") {
                    var output = json.RootElement.GetProperty("outputFps").GetDouble();
                    var source = json.RootElement.GetProperty("sourceFps").GetDouble();
                    _health = _health with { OutputFrameRate = output, InputFrameRate = source, UniqueFrameRate = source,
                        ConfiguredFrameRate = _config?.FrameRate ?? 0, FrameRateMode = _config?.FrameRateMode ?? "CFR",
                        Encoder = json.RootElement.TryGetProperty("encoder", out var encoder) ? encoder.GetString() ?? "" : "", EncoderProfile = _config?.EncoderProfile ?? "",
                        UpdatedUtc = DateTime.UtcNow };
                    HealthChanged?.Invoke(this, _health);
                }
            }
            await process.WaitForExitAsync(token); await errors;
            if (!_stopping) throw new IOException($"Private recorder exited {process.ExitCode}: " + (string.IsNullOrEmpty(lastFailure) ? "selected source or service was lost." : lastFailure));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _recording = false; _ready?.TrySetException(error);
            _readiness = _readiness with { Ready = false, Reason = error.Message };
            Publish(ReplayCaptureState.Failed, error.Message); RecordingStopped?.Invoke(this, EventArgs.Empty);
        }
    }
    private async Task<string?> CommandAsync(string name, object? data, CancellationToken token)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath ?? throw new InvalidOperationException("Recorder is stopped.")), token);
        await using var stream = new NetworkStream(socket, false);
        var id = Interlocked.Increment(ref _requestId);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, name, data }) + "\n");
        await stream.WriteAsync(bytes, token);
        using var reader = new StreamReader(stream);
        var response = await reader.ReadLineAsync(token) ?? throw new IOException("Recorder command disconnected.");
        using var json = JsonDocument.Parse(response);
        var e = json.RootElement;
        if (e.GetProperty("id").GetInt64() != id) throw new InvalidDataException("Recorder completion ID mismatch.");
        var result = e.TryGetProperty("data", out var value) ? value.GetString() : null;
        if (e.GetProperty("result").GetString() != "ok") throw new IOException(result ?? "Recorder command failed.");
        return result;
    }
    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default,
        string? titleOverride = null, ReplayClipWindow? clipWindow = null, string? gameDisplayNameOverride = null, Guid? saveId = null)
    {
        if (!_recording || _config is null) throw new InvalidOperationException("Replay is not recording.");
        var utcNow = DateTime.UtcNow;
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - _monotonicStart;
        var requested = LinuxClipInterval.FromRequest(now, utcNow, _config.DurationSeconds, clipWindow);
        if (!await _jobs.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Two Linux saves are already finalizing.");
        try
        {
            // Snapshot promptly; never queue a late snapshot behind a mux job.
            if (!await _snapshotGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("A replay snapshot is already being written. Retry after it completes.");
            string staging;
            try { if (!_recording) throw new InvalidOperationException("Replay stopped before the snapshot."); staging = await CommandAsync("save-replay", null, cancellationToken) ?? throw new IOException("Recorder returned no staging path."); }
            finally { _snapshotGate.Release(); }
            if (_staging is null || !Path.GetFullPath(staging).StartsWith(_staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Recorder staging path escaped its owned directory.");
            var id = saveId.GetValueOrDefault(); if (id == Guid.Empty) id = Guid.NewGuid();
            await File.WriteAllTextAsync(staging + ".request.json", JsonSerializer.Serialize(new { saveId = id, requested, _config, outputFolder, titleOverride, gameDisplayNameOverride }), cancellationToken);
            using var timeline = JsonDocument.Parse(await File.ReadAllTextAsync(staging + ".timeline.json", cancellationToken));
            var retainedStart = timeline.RootElement.GetProperty("start").GetDouble();
            var retainedEnd = timeline.RootElement.GetProperty("end").GetDouble();
            var interval = requested.InStaging(retainedStart, retainedEnd, 1.0 / _config.FrameRate);
            var game = gameDisplayNameOverride ?? _config.GameDisplayName;
            var directory = Path.Combine(outputFolder, ClipFileNaming.BuildBaseName(game)); Directory.CreateDirectory(directory);
            var path = ClipFileNaming.BuildUniquePath(directory, ClipFileNaming.BuildFileName(titleOverride ?? game, utcNow.ToLocalTime(), "mkv", _config.ClipFileNameScheme, _config.CustomClipFileNameTemplate, game));
            await LinuxClipFinalizer.FinalizeAsync(staging, path, interval, _config, false, cancellationToken);
            File.Delete(staging); File.Delete(staging + ".timeline.json"); File.Delete(staging + ".request.json");
            return path;
        }
        finally { _jobs.Release(); }
    }
    public async Task SetFullSessionEnabledAsync(bool enabled, CancellationToken token)
    {
        if (_session == enabled) return;
        if (!_recording || _config is null) throw new InvalidOperationException("Replay is not recording.");
        if (enabled) {
            lock (_gate) if (_finalizes.Count >= 2) throw new InvalidOperationException("Two sessions are still finalizing. Retry when one finishes.");
            var path = await CommandAsync("start-replay-recording", null, token) ?? throw new IOException("Session returned no staging path.");
            _session = true;
            await File.WriteAllTextAsync(path + ".session.json", JsonSerializer.Serialize(_config), token);
            return;
        }
        var staging = await CommandAsync("stop-replay-recording", null, token) ?? throw new IOException("Session returned no file.");
        _session = false;
        var task = QueueSessionFinalize(staging, _config);
        if (!_config.FullSessionBackgroundFinalize) await task;
    }
    private Task QueueSessionFinalize(string staging, ReplayBufferConfig config)
    {
        var task = FinalizeSessionAsync(staging, config);
        lock (_gate) _finalizes.Add(task);
        _ = task.ContinueWith(t => {
            lock (_gate) _finalizes.Remove(t);
            if (t.Exception is { } error) { AppLog.Error("Linux session staging could not finalize.", error); Publish(ReplayCaptureState.Degraded, error.GetBaseException().Message); }
        }, TaskScheduler.Default);
        return task;
    }
    private async Task FinalizeSessionAsync(string staging, ReplayBufferConfig config)
    {
        var directory = config.FullSessionRecordingFolder; Directory.CreateDirectory(directory);
        var path = ClipFileNaming.BuildUniquePath(directory, Path.GetFileName(staging));
        var started = DateTime.UtcNow;
        ReportProgress(path, new(path, 0, 0, config.VideoCodec != config.FullSessionVideoCodec, started));
        await _sessionFinalizers.WaitAsync();
        try
        {
            await LinuxClipFinalizer.FinalizeAsync(staging, path, null, config, true, CancellationToken.None,
                (processed, duration) => ReportProgress(path, new(path, duration, processed, config.VideoCodec != config.FullSessionVideoCodec, started)));
            ClipInfoSidecar.Save(config.LibraryFolder, path, new ClipInfo(config.GameDisplayName, null,
                "Session - " + config.GameDisplayName, DateTimeOffset.UtcNow, CaptureSource: config.CaptureSource));
            FullSessionQuotaService.Enforce(config);
            File.Delete(staging); File.Delete(staging + ".session.json");
        }
        catch (Exception error) { AppLog.Error($"Linux session finalization failed; staging retained: {staging}", error); Publish(ReplayCaptureState.Degraded, error.Message); }
        finally { _sessionFinalizers.Release(); ReportProgress(path, null); }
    }
    public async Task WaitForBackgroundFinalizeAsync(TimeSpan timeout)
    {
        Task[] tasks; lock (_gate) tasks = _finalizes.ToArray();
        await Task.WhenAll(tasks).WaitAsync(timeout);
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_recorder is null) return;
            _recording = false;
            // Drain the native snapshot only; FFmpeg work must not delay lock/suspend capture teardown.
            await _snapshotGate.WaitAsync(cancellationToken);
            _stopping = true;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (_session) {
                    var staging = await CommandAsync("stop-replay-recording", null, timeout.Token);
                    _session = false;
                    if (staging is not null && _config is not null) _ = QueueSessionFinalize(staging, _config);
                }
                if (!_recorder.HasExited) await CommandAsync("stop", null, timeout.Token);
            }
            finally {
                try { await TerminateAsync(); } finally { _snapshotGate.Release(); }
                _session = false;
                _readiness = _readiness with { Ready = false, Reason = "Capture stopped." };
                Publish(ReplayCaptureState.Stopped); RecordingStopped?.Invoke(this, EventArgs.Empty);
            }
            await _jobs.WaitAsync(cancellationToken);
            try { await _jobs.WaitAsync(cancellationToken); } catch { _jobs.Release(); throw; }
            _jobs.Release(2);
        }
        finally { _lifecycle.Release(); }
    }
    private async Task TerminateAsync()
    {
        var process = _recorder; _recorder = null;
        _stopping = true;
        _lifetime?.Cancel();
        if (process is not null)
        {
            if (!process.HasExited) {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
            }
            if (_observer is not null) await _observer;
            process.Dispose();
        }
        _lifetime?.Dispose(); _lifetime = null;
        if (_runtimeFolder is not null) { try { Directory.Delete(_runtimeFolder, true); } catch (IOException) { } }
    }
    // Focus freeze is owned by the compositor source; it must not pause audio or timestamps.
    public void SetCapturePaused(bool paused) { }
    public void Dispose() { StopAsync().GetAwaiter().GetResult(); }
}
