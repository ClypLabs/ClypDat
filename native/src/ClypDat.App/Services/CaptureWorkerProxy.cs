using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Avalonia.Threading;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed class CaptureWorkerProxy : IReplayBuffer, IReplayCaptureDiagnostics, IAdaptiveCaptureFrameRate, IReplayCaptureWorkerEvents, IReplayCaptureWorkerControl
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly List<DateTime> _failures = new();
    private readonly CaptureHealthRecoveryPolicy _fatalHealthPolicy = new();
    private readonly OutstandingReplaySaves _saves = new();
    private NamedPipeClientStream? _pipe;
    private Stream? _healthConnection;
    private Process? _process;
    private ReplayCaptureHealth _health = ReplayCaptureHealth.Unknown("Worker");
    private CancellationTokenSource? _recoveryCancellation;
    private Task? _recovery;
    private long _generation;
    private long _lostGeneration = -1;
    private CancellationTokenSource? _watchdogCancellation;
    private bool? _fullSessionEnabled;
    private string? _fullSessionContainer;
    private bool _gpuRecovering;
    private GraphicsFailure? _gpuFailure;
    private DateTime _lastGraphicsRecoveryUtc;
    private double _durationSeconds;
    // _isRecording is the replay's armed state as the app sees it: true while
    // the worker captures, and also while it holds capture suspended for an
    // unavailable display or session (_suspended). Only a user stop, the
    // crash-loop breaker or a failed recovery disarms it.
    private bool _isRecording, _suspended, _desiredRecording, _paused;
    private int? _frameRate;
    private int _fatalHealthRecoveryUsed;
    private string _hotkey = string.Empty;
    private string _fullSessionHotkey = string.Empty;
    private string? _clipGameName;
    private string? _autoClipGameId;
    private bool _autoClipEnabled;
    private IReadOnlyList<string> _autoClipEventIds = Array.Empty<string>();
    private OverlayCaptureSettings _videoOverlaySettings = OverlayCaptureSettings.None;
    private long _videoOverlayRevision;
    private volatile bool _disposed;

    public CaptureWorkerProxy(Func<ReplayBufferConfig> configProvider) => _configProvider = configProvider;
    public bool IsRecording => _isRecording;
    public bool IsCaptureSuspended => _suspended;
    public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, _durationSeconds));
    public bool LastSaveVideoWasFrozen => false;
    public event EventHandler? RecordingStopped;
    public event EventHandler? RecordingStateChanged;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;
    public event EventHandler<ReplaySaveStarted>? SaveStarted;
    public event EventHandler<ReplaySaveCompleted>? SaveCompleted;
    public event EventHandler<string>? FullSessionClosed;

    // Connection transitions refresh the shared recording presentation.
    private void RefreshRecordingState() => Dispatcher.UIThread.Post(() =>
        RecordingStateChanged?.Invoke(this, EventArgs.Empty));
    public event EventHandler<bool>? FullSessionRecordingToggled;
    public event EventHandler<AutoClipDetectorEvent>? AutoClipDetected;
    public event EventHandler<AutoClipDetectorStatus>? AutoClipStatusChanged;
    public ReplayCaptureHealth GetHealthSnapshot() => _health;
    internal long CaptureGeneration => Interlocked.Read(ref _generation);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ResetHealth();
        CancelRecovery(resetFailures: true);
        if (_recovery is { IsCompleted: false } previous) await previous.WaitAsync(cancellationToken);
        _fullSessionEnabled = null; _fullSessionContainer = null;
        _fatalHealthPolicy.Reset();
        Interlocked.Exchange(ref _fatalHealthRecoveryUsed, 0);
        _desiredRecording = true;
        try {
        await EnsureAttachedAsync(cancellationToken);
        var config = _configProvider();
        var attach = await AttachAsync(config, cancellationToken);
        if (!string.Equals(attach.ConfigIdentity, ReplayBufferConfigIdentity.Serialize(config), StringComparison.Ordinal))
        {
            if (attach.Recording) Accept(await SendAsync<CaptureWorkerAck>("stop", new { }, cancellationToken), "stop capture before applying new configuration");
            attach = await AttachAsync(config, cancellationToken);
            if (!string.Equals(attach.ConfigIdentity, ReplayBufferConfigIdentity.Serialize(config), StringComparison.Ordinal)) throw new InvalidOperationException("Capture worker did not apply requested capture configuration.");
        }
        ApplyAttach(attach, config, false);
        // An armed worker, capturing or suspended, needs no start: a suspended
        // one restarts capture by itself when the display or session returns.
        if (!_isRecording)
        {
            var started = await SendAsync<CaptureWorkerStartAck>("start", new { }, cancellationToken);
            AcceptStart(started, "start capture");
            ApplyStartAck(started);
            await SendAsync<CaptureWorkerAck>("pause", new { paused = _paused }, cancellationToken);
        }
        } catch (GraphicsDeviceUnavailableException error) {
            PublishHealth(_health with { GraphicsFailure = error.Failure });
            SetState(true, false);
            BeginRecovery(Volatile.Read(ref _generation), "graphics device unavailable", ExitCode(), true);
        }
        catch (IOException) when (_gpuRecovering && _desiredRecording) { SetState(true, false); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { _desiredRecording = false; _recoveryCancellation?.Cancel(); }
        CancelRecovery(false);
        if (_recovery is { IsCompleted: false } recovery) await recovery.WaitAsync(cancellationToken);
        if (_pipe?.IsConnected == true) try { Accept(await SendAsync<CaptureWorkerAck>("stop", new { }, cancellationToken), "stop capture"); } catch (IOException) { }
        SetRecording(false);
        ResetHealth();
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null, string? gameDisplayNameOverride = null, Guid? saveId = null)
    {
        if (_recovery is { IsCompleted: false } || _health.State == ReplayCaptureState.Recovering) throw new InvalidOperationException("Replay is recovering; retry after recording resumes.");
        if (!_desiredRecording || _health.State == ReplayCaptureState.Failed) throw new InvalidOperationException("Replay is not recording; no video can be saved.");
        // Capture stops while suspended, so there is no replay history to save.
        if (_suspended) throw new InvalidOperationException(SuspendedSaveMessage);
        await EnsureAttachedAsync(cancellationToken);
        var identity = saveId.GetValueOrDefault();
        if (identity == Guid.Empty) identity = Guid.NewGuid();
        var requestedUtc = DateTime.UtcNow;
        PublishSaveStarted(new(identity, requestedUtc));
        var result = await SendAsync<CaptureWorkerSaveResult>("save", new CaptureWorkerSaveRequest(outputFolder, titleOverride, clipWindow, gameDisplayNameOverride, identity, requestedUtc), cancellationToken);
        PublishSaveCompleted(ToCompletion(result, false));
        if (!string.IsNullOrWhiteSpace(result.Error)) throw new InvalidOperationException(result.Error);
        try { await SendAsync<CaptureWorkerAck>("ack-save", new CaptureWorkerSaveAcknowledgement(identity, result.Path), cancellationToken); }
        catch (IOException error) { AppLog.Info($"Completed replay save acknowledgement failed: {error.Message}"); }
        return result.Path;
    }

    public void SetCapturePaused(bool paused) { _paused = paused; _ = SendBestEffortAsync("pause", new { paused }); }
    public void RequestFrameRate(int frameRate) { _frameRate = frameRate; _ = SendBestEffortAsync("frame-rate", new { frameRate }); }

    public async Task ShutdownWorkerAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { _desiredRecording = false; _recoveryCancellation?.Cancel(); }
        if (_recovery is { IsCompleted: false } recovery) await recovery.WaitAsync(cancellationToken);
        if (_pipe?.IsConnected == true) try { await SendAsync<CaptureWorkerAck>("shutdown", new { }, cancellationToken); } catch (IOException) { }
        SetRecording(false); Disconnect();
    }

    // The worker acks "shutdown" before it drains pending saves and full-session
    // muxing, so the UI has to watch the process itself to know those files are
    // finished. Covers a worker this proxy started and one it reconnected to.
    public async Task<bool> WaitForWorkerExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var workers = new List<Process>();
        if (_process is { HasExited: false } own) workers.Add(own);
        else
        {
            var sessionId = Process.GetCurrentProcess().SessionId;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(CaptureWorkerExecutable.FileName)))
            {
                try
                {
                    if (process.SessionId == sessionId && process.Id != Environment.ProcessId) { workers.Add(process); continue; }
                }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
        if (workers.Count == 0) return true;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(workers.Select(worker => worker.WaitForExitAsync(timeoutSource.Token)));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            foreach (var worker in workers) if (!ReferenceEquals(worker, _process)) worker.Dispose();
        }
    }

    public async Task UpdateHotkeyAsync(string hotkey, CancellationToken cancellationToken = default)
    { _hotkey = hotkey; if (_recovery is { IsCompleted: false }) return; await EnsureAttachedAsync(cancellationToken); await SendAsync<CaptureWorkerAck>("hotkey", new { hotkey }, cancellationToken); }

    public async Task UpdateFullSessionContainerAsync(string container, CancellationToken cancellationToken = default)
    {
        _fullSessionContainer = container;
        if (_recovery is { IsCompleted: false }) return;
        if (_pipe is null) return;
        await SendAsync<CaptureWorkerAck>("full-session-container", new { container }, cancellationToken);
    }

    public async Task UpdateFullSessionHotkeyAsync(string hotkey, CancellationToken cancellationToken = default)
    { _fullSessionHotkey = hotkey; if (_recovery is { IsCompleted: false }) return; await EnsureAttachedAsync(cancellationToken); await SendAsync<CaptureWorkerAck>("full-session-hotkey", new { hotkey }, cancellationToken); }

    public async Task UpdateClipGameNameAsync(string gameDisplayName, CancellationToken cancellationToken = default)
    { _clipGameName = gameDisplayName; if (_recovery is { IsCompleted: false }) return; await EnsureAttachedAsync(cancellationToken); await SendAsync<CaptureWorkerAck>("clip-game-name", new { gameDisplayName }, cancellationToken); }

    public async Task UpdateAutoClipPolicyAsync(string? gameId, bool enabled, IReadOnlyList<string> enabledEventIds,
        CancellationToken cancellationToken = default)
    {
        _autoClipGameId = gameId;
        _autoClipEnabled = enabled;
        _autoClipEventIds = enabledEventIds.ToArray();
        if (_recovery is { IsCompleted: false }) return;
        await EnsureAttachedAsync(cancellationToken);
        Accept(await SendAsync<CaptureWorkerAck>("auto-clip-policy", new { gameId, enabled, enabledEventIds = _autoClipEventIds }, cancellationToken), "apply auto-clip policy");
    }

    public async Task UpdateVideoOverlaySettingsAsync(OverlayCaptureSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _videoOverlaySettings = settings with { Revision = NextOverlayRevision(ref _videoOverlayRevision), AppliedAtUtc = MonotonicClock.UtcNow };
        if (_recovery is { IsCompleted: false }) return;
        await EnsureAttachedAsync(cancellationToken);
        Accept(await SendAsync<CaptureWorkerAck>("video-overlays", _videoOverlaySettings, cancellationToken), "apply video overlays");
    }

    // Overlay revisions order settings across app instances too. The worker
    // outlives the app, so a relaunched app's first change must be newer than
    // anything the previous instance sent; the high-resolution timestamp is
    // shared by every process since boot.
    internal static long NextOverlayRevision(ref long last)
    {
        while (true)
        {
            var previous = Volatile.Read(ref last);
            var next = Math.Max(previous + 1, Stopwatch.GetTimestamp());
            if (Interlocked.CompareExchange(ref last, next, previous) == previous) return next;
        }
    }

    public void Dispose() { _disposed = true; _desiredRecording = false; CancelRecovery(false); Disconnect(); }

    private async Task EnsureAttachedAsync(CancellationToken cancellationToken, bool startProcess = true)
    {
        if (_pipe?.IsConnected == true) return;
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_pipe?.IsConnected == true) return;
            var pipe = CaptureWorkerPipe.CreateClient();
            try { await pipe.ConnectAsync(startProcess ? 500 : 2000, cancellationToken); }
            catch (TimeoutException) when (startProcess) { pipe.Dispose(); StartWorker(); pipe = CaptureWorkerPipe.CreateClient(); await pipe.ConnectAsync(5000, cancellationToken); }
            _pipe = pipe;
            var generation = Volatile.Read(ref _generation);
            _ = Task.Run(() => ReadLoopAsync(pipe, generation));
            var handshake = await SendAsync<CaptureWorkerHandshake>("handshake", new { ClientId = Environment.ProcessId }, cancellationToken);
            if (handshake.Version != CaptureWorkerProtocol.Version) throw new InvalidDataException("Capture worker protocol version mismatch.");
            if (handshake.ProcessId != 0 && (_process is null || _process.Id != handshake.ProcessId))
            {
                _process?.Dispose(); _process = Process.GetProcessById(handshake.ProcessId);
                _process.EnableRaisingEvents = true; var worker = _process;
                worker.Exited += (_, _) => BeginRecovery(generation, "process exited", ExitCode(worker));
            }
            var config = RecoveryConfiguration(); var attach = await AttachAsync(config, cancellationToken);
            ApplyAttach(attach, config, _desiredRecording);
            var gameDisplayName = _clipGameName ?? config.GameDisplayName;
            await SendAsync<CaptureWorkerAck>("clip-game-name", new { gameDisplayName }, cancellationToken);
            await SendAsync<CaptureWorkerAck>("full-session-hotkey", new { hotkey = string.IsNullOrWhiteSpace(_fullSessionHotkey) ? config.FullSessionHotkey : _fullSessionHotkey }, cancellationToken);
            foreach (var save in attach.UnacknowledgedSaves)
            {
                var completed = ToCompletion(save, isRecovered: true);
                PublishSaveCompleted(completed);
                // The worker empties the backlog as it hands it over, so this
                // is confirmation for older workers rather than the thing that
                // clears it. It must never abort the attach: this runs while a
                // redundant worker is exiting and taking the pipe down with it,
                // which is exactly when the ack fails.
                try
                {
                    await SendAsync<CaptureWorkerAck>("ack-save", new CaptureWorkerSaveAcknowledgement(completed.SaveId, completed.Path), cancellationToken);
                }
                catch (Exception error)
                {
                    AppLog.Info($"Capture worker ack-save after attach failed: {error.Message}");
                }
            }
            StartWatchdog(pipe, generation);
        }
        finally { _connectionGate.Release(); }
    }

    internal const string SuspendedSaveMessage = "Replay is suspended while the display or session is unavailable, so there is no video to save.";
    private Task<CaptureWorkerAttachResponse> AttachAsync(ReplayBufferConfig config, CancellationToken token)
        => SendAsync<CaptureWorkerAttachResponse>("attach", new CaptureWorkerAttachRequest(config, _videoOverlaySettings), token);
    internal void ApplyAttach(CaptureWorkerAttachResponse attach, ReplayBufferConfig config, bool preserveRecording)
    {
        _durationSeconds = config.DurationSeconds;
        if (!preserveRecording)
        {
            // The worker outlives the app: one still armed (capturing, or
            // suspended with capture requested) carries the user's intent.
            if (attach.Recording || attach.Suspended) _desiredRecording = true;
            ApplyWorkerState(attach.Recording, attach.Suspended);
        }
        PublishHealth(attach.Health with { RecoveryAttempt = _health.RecoveryAttempt, RecentWorkerFailureCount = _health.RecentWorkerFailureCount, LastWorkerExitCode = _health.LastWorkerExitCode });
    }
    internal void ApplyStartAck(CaptureWorkerStartAck started)
    {
        if (started.FullSession is { } session) PublishHealth(_health with { FullSession = session });
        ApplyWorkerState(started.Recording, started.Suspended);
    }
    private void AcceptStart(CaptureWorkerStartAck started, string action)
    {
        if (started.Accepted) return;
        if (started.FailureHealth is { } health) PublishHealth(health);
        if (started.FailureHealth?.GraphicsFailure is { Kind: GraphicsFailureKind.DeviceLost } failure)
            throw new GraphicsDeviceUnavailableException(started.Error, failure);
        throw new InvalidOperationException($"Capture worker failed to {action}: {started.Error}");
    }
    // A worker report of capture: active, or suspended for availability. A
    // suspension holds the armed state only while the user still wants replay.
    private void ApplyWorkerState(bool recording, bool suspended)
    {
        var held = !recording && suspended && _desiredRecording;
        SetState(recording || held, held);
    }
    private static void Accept(CaptureWorkerAck ack, string operation) { if (!ack.Accepted) throw new InvalidOperationException($"Capture worker failed to {operation}: {ack.Error}"); }

    internal async Task<T> SendAsync<T>(string type, object payload, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (type != "save") deadline.CancelAfter(type == "start" ? GraphicsRecovery.StartupTimeout : GraphicsRecovery.ControlTimeout);
        var callerToken = token; token = deadline.Token;
        if (_disposed) throw new IOException("Capture worker proxy is disposed.");
        var pipe = _pipe ?? throw new IOException("Capture worker pipe is not connected.");
        var id = Guid.NewGuid(); var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = completion;
        try
        {
            await _writeGate.WaitAsync(token); try { await CaptureWorkerPipe.WriteAsync(pipe, type, id, payload, token); } catch (ObjectDisposedException error) { throw new IOException("Capture worker pipe was disposed mid-send.", error); } finally { _writeGate.Release(); }
            var result = await completion.Task.WaitAsync(token);
            return result.Deserialize<T>() ?? throw new InvalidDataException($"Capture worker returned invalid {type} response.");
        }
        catch (OperationCanceledException error) when (!callerToken.IsCancellationRequested)
        {
            if (ReferenceEquals(_pipe, pipe)) { Disconnect(false); BeginRecovery(Volatile.Read(ref _generation), $"{type} timed out", ExitCode(), _gpuRecovering); }
            throw new IOException($"Capture worker {type} timed out.", error);
        }
        finally { lock (_pending) _pending.Remove(id); }
    }

    private async Task SendBestEffortAsync(string type, object payload)
    {
        if (_disposed || _recovery is { IsCompleted: false }) return;
        try { await EnsureAttachedAsync(CancellationToken.None); await SendAsync<CaptureWorkerAck>(type, payload, CancellationToken.None); }
        catch (Exception error) { AppLog.Info($"Capture worker {type} failed: {error.Message}"); }
    }

    internal async Task ReadLoopAsync(Stream pipe, long generation)
    {
        try
        {
            while (true)
            {
                var message = await CaptureWorkerPipe.ReadAsync(pipe, CancellationToken.None); if (message is null) break;
                if (!IsCurrentConnection(pipe, generation)) break;
                if (message.Type == "response") { TaskCompletionSource<JsonElement>? completion; lock (_pending) _pending.TryGetValue(message.RequestId, out completion); completion?.TrySetResult(message.Payload); continue; }
                switch (message.Type)
                {
                    case "health": var health = message.Payload.Deserialize<ReplayCaptureHealth>(); if (health is not null) Dispatcher.UIThread.Post(() => HandleWorkerHealth(pipe, generation, health)); break;
                    case "recording-state":
                        if (message.Payload.TryGetProperty("recording", out var recording))
                        {
                            var suspended = message.Payload.TryGetProperty("suspended", out var held) && held.ValueKind == JsonValueKind.True;
                            var active = recording.GetBoolean();
                            Dispatcher.UIThread.Post(() => HandleRecordingState(pipe, generation, active, suspended));
                        }
                        break;
                    case "recording-stopped": Dispatcher.UIThread.Post(() => { if (IsCurrentConnection(pipe, generation) && !_desiredRecording) { SetRecording(false); RecordingStopped?.Invoke(this, EventArgs.Empty); } }); break;
                    case "save-started":
                        var started = message.Payload.Deserialize<ReplaySaveStarted>();
                        if (started is not null)
                        {
                            if (started.SaveId == Guid.Empty) started = started with { SaveId = Guid.NewGuid() };
                            if (started.RequestedUtc == default) started = started with { RequestedUtc = DateTime.UtcNow };
                            AppLog.Info($"Capture worker event: save-started, id={started.SaveId}.");
                            PublishSaveStarted(started);
                        }
                        break;
                    case "save-completed":
                        var complete = message.Payload.Deserialize<CaptureWorkerSaveResult>();
                        if (complete is not null)
                        {
                            var completion = ToCompletion(complete, isRecovered: false);
                            AppLog.Info($"Capture worker event: save-completed, id={completion.SaveId}, path='{complete.Path}', error='{complete.Error}'.");
                            PublishSaveCompleted(completion);
                            _ = SendBestEffortAsync("ack-save", new CaptureWorkerSaveAcknowledgement(completion.SaveId, completion.Path));
                        }
                        break;
                    case "save-failed":
                        var failed = message.Payload.Deserialize<CaptureWorkerSaveResult>();
                        if (failed is not null)
                        {
                            var completion = ToCompletion(failed, isRecovered: false);
                            AppLog.Info($"Capture worker event: save-failed, id={completion.SaveId}, path='{failed.Path}', error='{failed.Error}'.");
                            PublishSaveCompleted(completion);
                        }
                        break;
                    case "full-session-toggled":
                        if (message.Payload.TryGetProperty("enabled", out var enabled)) {
                            var value = enabled.GetBoolean(); _fullSessionEnabled = value;
                            Dispatcher.UIThread.Post(() => { if (IsCurrentConnection(pipe, generation)) FullSessionRecordingToggled?.Invoke(this, value); });
                        }
                        break;
                    case "auto-clip-detected": var detected = message.Payload.Deserialize<AutoClipDetectorEvent>(); if (detected is not null) Dispatcher.UIThread.Post(() => { if (IsCurrentConnection(pipe, generation)) AutoClipDetected?.Invoke(this, detected); }); break;
                    case "auto-clip-status": var status = message.Payload.Deserialize<AutoClipDetectorStatus>(); if (status is not null) Dispatcher.UIThread.Post(() => { if (IsCurrentConnection(pipe, generation)) AutoClipStatusChanged?.Invoke(this, status); }); break;
                    case "full-session-closed":
                        var closedPath = message.Payload.GetString();
                        if (closedPath is not null) Dispatcher.UIThread.Post(() => FullSessionClosed?.Invoke(this, closedPath));
                        break;
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidDataException) { AppLog.Info($"Capture worker connection lost: {error.Message}"); }
        finally {
            if (ReferenceEquals(_pipe, pipe)) {
                FailPending(); _pipe = null; RefreshRecordingState();
                BeginRecovery(generation, "pipe closed", ExitCode(), _gpuRecovering || _health.GraphicsFailure?.Kind == GraphicsFailureKind.DeviceLost);
            }
            pipe.Dispose();
        }
    }

    private void PublishSaveStarted(ReplaySaveStarted save)
    { if (_saves.Begin(save)) Dispatcher.UIThread.Post(() => SaveStarted?.Invoke(this, save)); }
    private void PublishSaveCompleted(ReplaySaveCompleted save)
    { if (_saves.Complete(save)) Dispatcher.UIThread.Post(() => SaveCompleted?.Invoke(this, save)); }

    private void StartWatchdog(Stream pipe, long generation)
    {
        _watchdogCancellation?.Cancel(); _watchdogCancellation?.Dispose();
        var cancellation = _watchdogCancellation = new CancellationTokenSource();
        _ = WatchdogAsync(pipe, generation, cancellation.Token);
    }
    private async Task WatchdogAsync(Stream pipe, long generation, CancellationToken token)
    {
        try {
            while (IsCurrentConnection(pipe, generation)) {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                if (!IsCurrentConnection(pipe, generation)) return;
                var health = await SendAsync<ReplayCaptureHealth>("health", new { }, token);
                if (health.GraphicsFailure?.Kind == GraphicsFailureKind.DeviceLost) {
                    PublishHealth(health); BeginRecovery(generation, "graphics device removed", ExitCode(), true); return;
                }
                // A live IPC loop with a hung native control thread is not healthy.
                if (_desiredRecording && !_suspended && health.State is not ReplayCaptureState.Stopped &&
                    DateTime.UtcNow - health.UpdatedUtc > GraphicsRecovery.ControlTimeout) {
                    BeginRecovery(generation, "native health stalled", ExitCode(), _gpuRecovering); return;
                }
            }
        } catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Info($"Capture health watchdog failed: {error.Message}"); BeginRecovery(generation, "health watchdog lost worker", ExitCode(), _gpuRecovering); }
    }

    private static ReplaySaveCompleted ToCompletion(CaptureWorkerSaveResult save, bool isRecovered)
    {
        var saveId = save.SaveId.GetValueOrDefault();
        if (saveId == Guid.Empty) saveId = Guid.NewGuid();
        return new ReplaySaveCompleted(saveId, save.Path, save.Title, save.RequestedUtc ?? save.CompletedUtc, save.CompletedUtc, save.Error, isRecovered);
    }

    private void StartWorker(CancellationToken token = default)
    {
        lock (_gate) {
        token.ThrowIfCancellationRequested();
        if (_process is { HasExited: false }) return;
        var appPath = Environment.ProcessPath ?? throw new InvalidOperationException("ClypDat process path unavailable.");
        var path = CaptureWorkerExecutable.Resolve(appPath);
        var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--capture-worker" } }) ?? throw new InvalidOperationException("Capture worker did not start.");
        var generation = Interlocked.Increment(ref _generation); _lostGeneration = -1; _process = process;
        process.EnableRaisingEvents = true; process.Exited += (_, _) => BeginRecovery(generation, "process exited", ExitCode(process));
        }
    }

    private void BeginRecovery(long generation, string reason, int? exitCode, bool gpu = false)
    {
        if (_disposed || !_desiredRecording || generation != Volatile.Read(ref _generation)) return;
        lock (_gate)
        {
            if (_lostGeneration == generation || _recovery is { IsCompleted: false }) return;
            _lostGeneration = generation; _recoveryCancellation?.Cancel(); _recoveryCancellation = new CancellationTokenSource();
            if (gpu) { _gpuRecovering = true; _gpuFailure ??= _health.GraphicsFailure; }
            var cancellation = _recoveryCancellation;
            _recovery = Task.Run(() => gpu ? RecoverGpuAsync(exitCode, cancellation.Token) : RecoverAsync(generation, reason, exitCode, cancellation.Token));
        }
    }

    internal void HandleRecordingState(Stream pipe, long generation, bool recording, bool suspended)
    {
        if (IsCurrentConnection(pipe, generation)) ApplyWorkerState(recording, suspended);
    }

    private bool IsCurrentConnection(Stream pipe, long generation) =>
        !_disposed && ReferenceEquals(_pipe, pipe) && generation == Volatile.Read(ref _generation);

    internal void HandleWorkerHealth(Stream pipe, long generation, ReplayCaptureHealth health)
    {
        // UI callbacks already queued by the lost connection can arrive after
        // KillWorker. They must not consume the replacement worker's recovery.
        if (!IsCurrentConnection(pipe, generation)) return;
        if (!ReferenceEquals(_healthConnection, pipe))
        {
            _healthConnection = pipe;
            _fatalHealthPolicy.Reset();
        }
        PublishHealth(health);
        if (_desiredRecording && health.GraphicsFailure?.Kind == GraphicsFailureKind.DeviceLost) {
            BeginRecovery(generation, "graphics device removed", ExitCode(), true); return;
        }
        if (_recovery is { IsCompleted: false }) return;
        // Suspended capture is stopped on purpose: throughput is not a fault.
        if (!_desiredRecording || _suspended)
        {
            _fatalHealthPolicy.Reset();
            return;
        }
        if (!_fatalHealthPolicy.Observe(health)) return;

        AppLog.Error($"Capture health recovery triggered: backend={health.Backend}, source={health.CaptureMode}, state={health.State}, failure={health.LastFailure}, encoder={health.Encoder}, adapter={health.AdapterDescription}, inputFps={health.InputFrameRate:F1}, uniqueFps={health.UniqueFrameRate:F1}, outputFps={health.OutputFrameRate:F1}, targetFps={health.TargetFrameRate}, queue={health.QueueDepth}/{health.EncodeQueueCapacity}, dropped={health.DroppedFrames}, submissionStalled={health.EncoderSubmissionStalled}, stage={health.BottleneckStage}, recovery={health.PipelineRecoveryAction}, attempt={health.RecoveryAttempt}, paused={health.CapturePaused}.");

        if (Interlocked.CompareExchange(ref _fatalHealthRecoveryUsed, 1, 0) == 0)
        {
            AppLog.Info("Capture worker health requested recovery; restarting worker once.");
            BeginRecovery(Volatile.Read(ref _generation), "fatal encoder health", ExitCode());
            return;
        }

        _desiredRecording = false;
        RecoveryHealth(1, _health.RecentWorkerFailureCount, _health.LastWorkerExitCode, null, true, ReplayRecoveryStopReason.CapturePipelineStall,
            string.IsNullOrWhiteSpace(health.LastFailure)
                ? "Capture throughput remained unhealthy after worker recovery; recording stopped."
                : $"Capture failed after worker recovery: {health.LastFailure}");
        SetRecording(false);
        RecordingStopped?.Invoke(this, EventArgs.Empty);
    }

    private async Task RecoverAsync(long lostGeneration, string reason, int? exitCode, CancellationToken token)
    {
        var now = DateTime.UtcNow; int count;
        lock (_gate) { _failures.RemoveAll(time => now - time > TimeSpan.FromMinutes(2)); _failures.Add(now); count = _failures.Count; }
        AppLog.Info($"Capture worker lost ({reason}, exit={exitCode?.ToString() ?? "unknown"}), failures={count}.");
        if (count >= 5) { Breaker(count, exitCode); return; }
        RecoveryHealth(0, count, exitCode, DateTime.UtcNow, false, ReplayRecoveryStopReason.None, $"Capture worker lost: {reason}");
        try
        {
            using var reconnect = CancellationTokenSource.CreateLinkedTokenSource(token); reconnect.CancelAfter(TimeSpan.FromSeconds(2));
            try { await EnsureAttachedAsync(reconnect.Token, false); await RestoreAsync(token); AppLog.Info("Capture worker IPC reconnect succeeded."); return; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception error) { AppLog.Info($"Capture worker IPC reconnect failed: {error.Message}"); }
            await TerminateWorkerAsync(token);
            for (var attempt = 0; attempt < RetryDelays.Length; attempt++)
            {
                var delay = RetryDelays[attempt]; RecoveryHealth(attempt + 1, count, exitCode, DateTime.UtcNow + delay, false, ReplayRecoveryStopReason.None, $"Restarting capture worker in {delay.TotalSeconds:0}s.");
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                // This recovery owns replacement generations too. Do not
                // abandon retry merely because StartWorker advanced generation.
                if (!_desiredRecording) return;
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(token); startup.CancelAfter(GraphicsRecovery.StartupTimeout);
                try { StartWorker(startup.Token); await EnsureAttachedAsync(startup.Token); await RestoreAsync(startup.Token); AppLog.Info("Capture worker recovery succeeded."); return; }
                catch (GraphicsDeviceUnavailableException error) {
                    _gpuRecovering = true; _gpuFailure = error.Failure;
                    await RecoverGpuAsync(exitCode, token); return;
                }
                catch (Exception error) { AppLog.Info($"Capture worker restart attempt {attempt + 1} failed: {error.Message}"); await TerminateWorkerAsync(token); }
            }
            Breaker(count, exitCode);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RecoverGpuAsync(int? exitCode, CancellationToken token)
    {
        try {
            _gpuRecovering = true;
            _gpuFailure ??= _health.GraphicsFailure;
            SetState(true, _suspended);
            RecoveryHealth(0, _health.RecentWorkerFailureCount, exitCode, DateTime.UtcNow, false, ReplayRecoveryStopReason.None, "Waiting for graphics device. Replay buffer will reset.");
            // Existing save jobs use pinned media, not the lost capture device.
            // Keep the pipe responsive until they finish, for at most 30 seconds.
            var drain = Stopwatch.StartNew();
            while (_saves.Any && _pipe?.IsConnected == true && drain.Elapsed < GraphicsRecovery.SaveDrainTimeout) await Task.Delay(100, token);
            await GraphicsRecovery.RunAsync(async (attempt, startupToken) => {
                try {
                    await TerminateWorkerAsync(startupToken);
                    if (!_desiredRecording) return true;
                    StartWorker(startupToken); await EnsureAttachedAsync(startupToken); await RestoreAsync(startupToken);
                    token.ThrowIfCancellationRequested();
                    _gpuRecovering = false; _gpuFailure = null;
                    _lastGraphicsRecoveryUtc = DateTime.UtcNow;
                    PublishHealth(_health with { State = _suspended ? ReplayCaptureState.Stopped : ReplayCaptureState.Healthy,
                        GraphicsFailure = null, PipelineRecoveryAction = ReplayPipelineRecoveryAction.None,
                        LastFailure = "Replay buffer reset after graphics recovery.", ReplayBufferReset = true, NextWorkerRetryUtc = null });
                    AppLog.Info("Graphics device recovery succeeded with a fresh replay buffer."); return true;
                } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { AppLog.Info($"Graphics recovery attempt {attempt + 1} failed: {error.Message}"); return false; }
            }, token, (attempt, delay) => RecoveryHealth(attempt + 1, _health.RecentWorkerFailureCount, exitCode, DateTime.UtcNow + delay, false,
                ReplayRecoveryStopReason.None, "Waiting for graphics device. Replay buffer will reset."));
        } catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Graphics recovery stopped unexpectedly.", error); }
        finally { if (token.IsCancellationRequested) { _gpuRecovering = false; _gpuFailure = null; } }
    }

    private ReplayBufferConfig RecoveryConfiguration()
    {
        var config = _configProvider();
        return config with { FullSessionRecordingEnabled = _fullSessionEnabled ?? config.FullSessionRecordingEnabled,
            FullSessionContainer = _fullSessionContainer ?? config.FullSessionContainer };
    }

    internal async Task RestoreAsync(CancellationToken token)
    {
        var config = RecoveryConfiguration(); var attach = await AttachAsync(config, token); ApplyAttach(attach, config, true);
        if (!string.Equals(attach.ConfigIdentity, ReplayBufferConfigIdentity.Serialize(config), StringComparison.Ordinal)) {
            Accept(await SendAsync<CaptureWorkerAck>("stop", new { }, token), "stop capture before restoring configuration");
            attach = await AttachAsync(config, token);
            if (!string.Equals(attach.ConfigIdentity, ReplayBufferConfigIdentity.Serialize(config), StringComparison.Ordinal)) throw new InvalidOperationException("Capture worker did not restore the requested configuration.");
        }
        await SendAsync<CaptureWorkerAck>("hotkey", new { hotkey = string.IsNullOrWhiteSpace(_hotkey) ? config.SaveReplayHotkey : _hotkey }, token);
        await SendAsync<CaptureWorkerAck>("full-session-hotkey", new { hotkey = string.IsNullOrWhiteSpace(_fullSessionHotkey) ? config.FullSessionHotkey : _fullSessionHotkey }, token);
        Accept(await SendAsync<CaptureWorkerAck>("auto-clip-policy", new { gameId = _autoClipGameId, enabled = _autoClipEnabled, enabledEventIds = _autoClipEventIds }, token), "restore auto-clip policy");
        // A worker that kept capture requested through a suspension resumes on its own.
        if (_desiredRecording && !attach.Recording && !attach.Suspended)
        {
            var started = await SendAsync<CaptureWorkerStartAck>("start", new { }, token);
            AcceptStart(started, "restart capture");
            ApplyStartAck(started);
        }
        else if (attach.Suspended) ApplyWorkerState(false, true);
        if (!string.Equals(ReplayBufferConfigIdentity.Serialize(config), ReplayBufferConfigIdentity.Serialize(RecoveryConfiguration()), StringComparison.Ordinal)) {
            await TerminateWorkerAsync(token); throw new InvalidOperationException("Capture configuration changed during recovery.");
        }
        await SendAsync<CaptureWorkerAck>("pause", new { paused = _paused }, token);
        if (_frameRate is int frameRate) await SendAsync<CaptureWorkerAck>("frame-rate", new { frameRate }, token);
        await SendAsync<CaptureWorkerAck>("video-overlays", _videoOverlaySettings, token);
        await SendAsync<CaptureWorkerAck>("clip-game-name", new { gameDisplayName = _clipGameName ?? config.GameDisplayName }, token);
    }

    private void Breaker(int count, int? exitCode)
    { _desiredRecording = false; RecoveryHealth(RetryDelays.Length, count, exitCode, null, true, ReplayRecoveryStopReason.WorkerCrashLoop, "Capture worker crashed repeatedly."); Dispatcher.UIThread.Post(() => { SetRecording(false); RecordingStopped?.Invoke(this, EventArgs.Empty); }); AppLog.Info("Capture worker recovery breaker opened."); }
    private void RecoveryHealth(int attempt, int count, int? exitCode, DateTime? retry, bool breaker, ReplayRecoveryStopReason stopReason, string failure) => PublishHealth(_health with { State = breaker ? ReplayCaptureState.Failed : ReplayCaptureState.Recovering, RecoveryAttempt = attempt, RecentWorkerFailureCount = count, LastWorkerExitCode = exitCode, NextWorkerRetryUtc = retry, WorkerCrashLoopDetected = stopReason == ReplayRecoveryStopReason.WorkerCrashLoop, RecoveryStopReason = stopReason, LastFailure = failure, UpdatedUtc = DateTime.UtcNow });
    private void SetRecording(bool value) => SetState(value, false);
    private void SetState(bool armed, bool suspended)
    {
        if (_isRecording == armed && _suspended == suspended) return;
        _isRecording = armed; _suspended = suspended;
        RecordingStateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void PublishHealth(ReplayCaptureHealth health) {
        if (_gpuRecovering) health = health with { State = ReplayCaptureState.Recovering, GraphicsFailure = _gpuFailure ?? health.GraphicsFailure, ReplayBufferReset = true };
        else if (_lastGraphicsRecoveryUtc != default && DateTime.UtcNow - _lastGraphicsRecoveryUtc < TimeSpan.FromSeconds(30)) health = health with { ReplayBufferReset = true };
        _health = health; HealthChanged?.Invoke(this, health);
    }
    private void ResetHealth() => PublishHealth(ReplayCaptureHealth.Unknown("Worker"));
    private void FailPending() { lock (_pending) foreach (var item in _pending.Values) item.TrySetException(new IOException("Capture worker connection closed.")); }
    private int? ExitCode(Process? process = null) { try { return (process ?? _process) is { HasExited: true } item ? item.ExitCode : null; } catch { return null; } }
    private async Task TerminateWorkerAsync(CancellationToken token)
    {
        var process = _process ?? FindWorker();
        if (process is not null) {
            if (!process.HasExited) {
                if (_pipe?.IsConnected == true) {
                    try { await SendAsync<CaptureWorkerAck>("shutdown", new { }, token); }
                    catch (IOException) { }
                }
                try { await process.WaitForExitAsync(token).WaitAsync(GraphicsRecovery.ControlTimeout, token); }
                catch (TimeoutException) {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
                }
            }
            if (!process.HasExited) throw new IOException("Previous capture worker has not exited.");
            if (ReferenceEquals(_process, process)) _process = null;
            process.Dispose();
        }
        Disconnect(false);
        foreach (var interrupted in _saves.Interrupt(DateTime.UtcNow)) Dispatcher.UIThread.Post(() => SaveCompleted?.Invoke(this, interrupted));
    }
    private static Process? FindWorker()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var expected = CaptureWorkerExecutable.Resolve(Environment.ProcessPath ?? "");
        foreach (var worker in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(CaptureWorkerExecutable.FileName))) {
            try {
                if (worker.SessionId == session && string.Equals(worker.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return worker;
            } catch { }
            worker.Dispose();
        }
        return null;
    }
    private void CancelRecovery(bool resetFailures) { lock (_gate) { _recoveryCancellation?.Cancel(); if (resetFailures) _failures.Clear(); } }
    private void Disconnect(bool resetHealth = true) {
        _watchdogCancellation?.Cancel();
        var pipe = _pipe; _pipe = null;
        try { pipe?.Dispose(); } catch { }
        FailPending(); if (resetHealth) ResetHealth();
    }
}

internal static class CaptureWorkerExecutable
{
    internal const string FileName = "ClypDatRecorder.exe";

    internal static string Resolve(string appPath, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var workerPath = Path.Combine(Path.GetDirectoryName(appPath) ?? string.Empty, FileName);
        return fileExists(workerPath) ? workerPath : appPath;
    }
}
