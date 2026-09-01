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
    private NamedPipeClientStream? _pipe;
    private Process? _process;
    private ReplayCaptureHealth _health = ReplayCaptureHealth.Unknown("Worker");
    private CancellationTokenSource? _recoveryCancellation;
    private Task? _recovery;
    private long _generation;
    private long _lostGeneration = -1;
    private double _durationSeconds;
    private bool _isRecording, _desiredRecording, _paused;
    private int? _frameRate;
    private int _fatalHealthRecoveryUsed;
    private string _hotkey = string.Empty;
    private volatile bool _disposed;

    public CaptureWorkerProxy(Func<ReplayBufferConfig> configProvider) => _configProvider = configProvider;
    public bool IsRecording => _isRecording;
    public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, _durationSeconds));
    public bool LastSaveVideoWasFrozen => false;
    public event EventHandler? RecordingStopped;
    public event EventHandler? RecordingStateChanged;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;
    public event EventHandler? SaveStarted;
    public event EventHandler<ReplaySaveCompleted>? SaveCompleted;
    public ReplayCaptureHealth GetHealthSnapshot() => _health;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CancelRecovery(resetFailures: true);
        _fatalHealthPolicy.Reset();
        Interlocked.Exchange(ref _fatalHealthRecoveryUsed, 0);
        _desiredRecording = true;
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
        if (!_isRecording) { Accept(await SendAsync<CaptureWorkerAck>("start", new { }, cancellationToken), "start capture"); SetRecording(true); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _desiredRecording = false;
        CancelRecovery(false);
        if (_pipe?.IsConnected == true) try { Accept(await SendAsync<CaptureWorkerAck>("stop", new { }, cancellationToken), "stop capture"); } catch (IOException) { }
        SetRecording(false);
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null)
    {
        if (_recovery is { IsCompleted: false } || _health.State == ReplayCaptureState.Recovering) throw new InvalidOperationException("Replay is recovering; retry after recording resumes.");
        if (!_desiredRecording || _health.State == ReplayCaptureState.Failed) throw new InvalidOperationException("Replay is not recording; no video can be saved.");
        await EnsureAttachedAsync(cancellationToken);
        var result = await SendAsync<CaptureWorkerSaveResult>("save", new CaptureWorkerSaveRequest(outputFolder, titleOverride, clipWindow), cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error)) throw new InvalidOperationException(result.Error);
        await SendAsync<CaptureWorkerAck>("ack-save", new { result.Path }, cancellationToken);
        return result.Path;
    }

    public void SetCapturePaused(bool paused) { _paused = paused; _ = SendBestEffortAsync("pause", new { paused }); }
    public void RequestFrameRate(int frameRate) { _frameRate = frameRate; _ = SendBestEffortAsync("frame-rate", new { frameRate }); }

    public async Task ShutdownWorkerAsync(CancellationToken cancellationToken = default)
    {
        _desiredRecording = false; CancelRecovery(false);
        if (_pipe?.IsConnected == true) try { await SendAsync<CaptureWorkerAck>("shutdown", new { }, cancellationToken); } catch (IOException) { }
        SetRecording(false); Disconnect();
    }

    public async Task UpdateHotkeyAsync(string hotkey, CancellationToken cancellationToken = default)
    { _hotkey = hotkey; await EnsureAttachedAsync(cancellationToken); await SendAsync<CaptureWorkerAck>("hotkey", new { hotkey }, cancellationToken); }

    public void Dispose() { _disposed = true; _desiredRecording = false; CancelRecovery(false); Disconnect(); }

    private async Task EnsureAttachedAsync(CancellationToken cancellationToken, bool startProcess = true)
    {
        if (_pipe?.IsConnected == true) return;
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_pipe?.IsConnected == true) return;
            if (startProcess) StartWorker();
            var pipe = CaptureWorkerPipe.CreateClient();
            await pipe.ConnectAsync(5000, cancellationToken);
            _pipe = pipe;
            var generation = Volatile.Read(ref _generation);
            _ = Task.Run(() => ReadLoopAsync(pipe, generation));
            await SendAsync<CaptureWorkerHandshake>("handshake", new { ClientId = Environment.ProcessId }, cancellationToken);
            var config = _configProvider(); var attach = await AttachAsync(config, cancellationToken);
            ApplyAttach(attach, config, _desiredRecording);
            foreach (var save in attach.UnacknowledgedSaves)
            { SaveCompleted?.Invoke(this, new ReplaySaveCompleted(save.Path, save.Title, save.CompletedUtc, save.Error)); await SendAsync<CaptureWorkerAck>("ack-save", new { save.Path }, cancellationToken); }
        }
        finally { _connectionGate.Release(); }
    }

    private Task<CaptureWorkerAttachResponse> AttachAsync(ReplayBufferConfig config, CancellationToken token) => SendAsync<CaptureWorkerAttachResponse>("attach", config, token);
    private void ApplyAttach(CaptureWorkerAttachResponse attach, ReplayBufferConfig config, bool preserveRecording)
    {
        _durationSeconds = config.DurationSeconds;
        if (!preserveRecording) SetRecording(attach.Recording);
        PublishHealth(attach.Health with { RecoveryAttempt = _health.RecoveryAttempt, RecentWorkerFailureCount = _health.RecentWorkerFailureCount, LastWorkerExitCode = _health.LastWorkerExitCode });
    }
    private static void Accept(CaptureWorkerAck ack, string operation) { if (!ack.Accepted) throw new InvalidOperationException($"Capture worker failed to {operation}: {ack.Error}"); }

    private async Task<T> SendAsync<T>(string type, object payload, CancellationToken token)
    {
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
        finally { lock (_pending) _pending.Remove(id); }
    }

    private async Task SendBestEffortAsync(string type, object payload)
    {
        if (_disposed || _recovery is { IsCompleted: false }) return;
        try { await EnsureAttachedAsync(CancellationToken.None); await SendAsync<CaptureWorkerAck>(type, payload, CancellationToken.None); }
        catch (Exception error) { AppLog.Info($"Capture worker {type} failed: {error.Message}"); }
    }

    private async Task ReadLoopAsync(Stream pipe, long generation)
    {
        try
        {
            while (true)
            {
                var message = await CaptureWorkerPipe.ReadAsync(pipe, CancellationToken.None); if (message is null) break;
                if (message.Type == "response") { TaskCompletionSource<JsonElement>? completion; lock (_pending) _pending.TryGetValue(message.RequestId, out completion); completion?.TrySetResult(message.Payload); continue; }
                switch (message.Type)
                {
                    case "health": var health = message.Payload.Deserialize<ReplayCaptureHealth>(); if (health is not null) Dispatcher.UIThread.Post(() => HandleWorkerHealth(health)); break;
                    case "recording-stopped": Dispatcher.UIThread.Post(() => { if (!_desiredRecording) { SetRecording(false); RecordingStopped?.Invoke(this, EventArgs.Empty); } }); break;
                    case "save-started": Dispatcher.UIThread.Post(() => SaveStarted?.Invoke(this, EventArgs.Empty)); break;
                    case "save-completed": var complete = message.Payload.Deserialize<CaptureWorkerSaveResult>(); if (complete is not null) { Dispatcher.UIThread.Post(() => SaveCompleted?.Invoke(this, new ReplaySaveCompleted(complete.Path, complete.Title, complete.CompletedUtc, complete.Error))); _ = SendBestEffortAsync("ack-save", new { complete.Path }); } break;
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidDataException) { AppLog.Info($"Capture worker connection lost: {error.Message}"); }
        finally { FailPending(); if (ReferenceEquals(_pipe, pipe)) _pipe = null; BeginRecovery(generation, "pipe closed", ExitCode()); }
    }

    private void StartWorker()
    {
        if (_process is { HasExited: false }) return;
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("ClypDat process path unavailable.");
        var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--capture-worker" } }) ?? throw new InvalidOperationException("Capture worker did not start.");
        var generation = Interlocked.Increment(ref _generation); _lostGeneration = -1; _process = process;
        process.EnableRaisingEvents = true; process.Exited += (_, _) => BeginRecovery(generation, "process exited", ExitCode(process));
    }

    private void BeginRecovery(long generation, string reason, int? exitCode)
    {
        if (_disposed || !_desiredRecording || generation != Volatile.Read(ref _generation)) return;
        lock (_gate)
        {
            if (_lostGeneration == generation || _recovery is { IsCompleted: false }) return;
            _lostGeneration = generation; _recoveryCancellation?.Cancel(); _recoveryCancellation = new CancellationTokenSource();
            _recovery = RecoverAsync(generation, reason, exitCode, _recoveryCancellation.Token);
        }
    }

    private void HandleWorkerHealth(ReplayCaptureHealth health)
    {
        PublishHealth(health);
        if (!_fatalHealthPolicy.Observe(health)) return;

        if (Interlocked.CompareExchange(ref _fatalHealthRecoveryUsed, 1, 0) == 0)
        {
            AppLog.Info("Capture worker health fatal for three windows; restarting worker once.");
            KillWorker();
            BeginRecovery(Volatile.Read(ref _generation), "fatal encoder health", ExitCode());
            return;
        }

        _desiredRecording = false;
        RecoveryHealth(1, _health.RecentWorkerFailureCount, _health.LastWorkerExitCode, null, true, ReplayRecoveryStopReason.CapturePipelineStall,
            "Capture output remained below 1 FPS with a full encoder queue; recording stopped.");
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
            KillWorker();
            for (var attempt = 0; attempt < RetryDelays.Length; attempt++)
            {
                var delay = RetryDelays[attempt]; RecoveryHealth(attempt + 1, count, exitCode, DateTime.UtcNow + delay, false, ReplayRecoveryStopReason.None, $"Restarting capture worker in {delay.TotalSeconds:0}s.");
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                // This recovery owns replacement generations too. Do not
                // abandon retry merely because StartWorker advanced generation.
                if (!_desiredRecording) return;
                try { StartWorker(); await EnsureAttachedAsync(token); await RestoreAsync(token); AppLog.Info("Capture worker recovery succeeded."); return; }
                catch (Exception error) { AppLog.Info($"Capture worker restart attempt {attempt + 1} failed: {error.Message}"); KillWorker(); }
            }
            Breaker(count, exitCode);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RestoreAsync(CancellationToken token)
    {
        var config = _configProvider(); var attach = await AttachAsync(config, token); ApplyAttach(attach, config, true);
        await SendAsync<CaptureWorkerAck>("hotkey", new { hotkey = string.IsNullOrWhiteSpace(_hotkey) ? config.SaveReplayHotkey : _hotkey }, token);
        await SendAsync<CaptureWorkerAck>("pause", new { paused = _paused }, token);
        if (_frameRate is int frameRate) await SendAsync<CaptureWorkerAck>("frame-rate", new { frameRate }, token);
        if (_desiredRecording && !attach.Recording) Accept(await SendAsync<CaptureWorkerAck>("start", new { }, token), "restart capture");
        if (_desiredRecording) SetRecording(true);
    }

    private void Breaker(int count, int? exitCode)
    { _desiredRecording = false; RecoveryHealth(RetryDelays.Length, count, exitCode, null, true, ReplayRecoveryStopReason.WorkerCrashLoop, "Capture worker crashed repeatedly."); Dispatcher.UIThread.Post(() => { SetRecording(false); RecordingStopped?.Invoke(this, EventArgs.Empty); }); AppLog.Info("Capture worker recovery breaker opened."); }
    private void RecoveryHealth(int attempt, int count, int? exitCode, DateTime? retry, bool breaker, ReplayRecoveryStopReason stopReason, string failure) => PublishHealth(_health with { State = breaker ? ReplayCaptureState.Failed : ReplayCaptureState.Recovering, RecoveryAttempt = attempt, RecentWorkerFailureCount = count, LastWorkerExitCode = exitCode, NextWorkerRetryUtc = retry, WorkerCrashLoopDetected = stopReason == ReplayRecoveryStopReason.WorkerCrashLoop, RecoveryStopReason = stopReason, LastFailure = failure, UpdatedUtc = DateTime.UtcNow });
    private void SetRecording(bool value) { if (_isRecording == value) return; _isRecording = value; RecordingStateChanged?.Invoke(this, EventArgs.Empty); }
    private void PublishHealth(ReplayCaptureHealth health) { _health = health; HealthChanged?.Invoke(this, health); }
    private void FailPending() { lock (_pending) foreach (var item in _pending.Values) item.TrySetException(new IOException("Capture worker connection closed.")); }
    private int? ExitCode(Process? process = null) { try { return (process ?? _process) is { HasExited: true } item ? item.ExitCode : null; } catch { return null; } }
    private void KillWorker() { var process = _process; if (process is null) return; try { if (!process.HasExited) process.Kill(true); } catch { } try { process.Dispose(); } catch { } if (ReferenceEquals(_process, process)) _process = null; Disconnect(); }
    private void CancelRecovery(bool resetFailures) { lock (_gate) { _recoveryCancellation?.Cancel(); if (resetFailures) _failures.Clear(); } }
    private void Disconnect() { try { _pipe?.Dispose(); } catch { } _pipe = null; }
}
