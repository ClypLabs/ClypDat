using System.Diagnostics;
using System.Text.Json;
using System.IO.Pipes;
using Avalonia.Threading;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed class CaptureWorkerProxy : IReplayBuffer, IReplayCaptureDiagnostics, IAdaptiveCaptureFrameRate, IReplayCaptureWorkerEvents, IReplayCaptureWorkerControl
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<Guid, TaskCompletionSource<JsonElement>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private Process? _process;
    private Task? _reader;
    private ReplayCaptureHealth _health = ReplayCaptureHealth.Unknown("Worker");
    private bool _isRecording;
    private bool _disposed;

    public CaptureWorkerProxy(Func<ReplayBufferConfig> configProvider) => _configProvider = configProvider;

    public bool IsRecording => _isRecording;
    public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, _health.UpdatedUtc == default ? 0 : _durationSeconds));
    private double _durationSeconds;
    public event EventHandler? RecordingStopped;
    public event EventHandler? RecordingStateChanged;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;
    public event EventHandler? SaveStarted;
    public event EventHandler<ReplaySaveCompleted>? SaveCompleted;
    public ReplayCaptureHealth GetHealthSnapshot() => _health;
    public bool LastSaveVideoWasFrozen => false;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAttachedAsync(cancellationToken);

        var config = _configProvider();
        var attach = await AttachConfigAsync(config, cancellationToken);
        if (!string.Equals(attach.ConfigIdentity, ReplayBufferConfigIdentity.Serialize(config), StringComparison.Ordinal))
        {
            if (attach.Recording)
            {
                var stop = await SendAsync<CaptureWorkerAck>("stop", new { }, cancellationToken);
                EnsureAccepted(stop, "stop capture before applying new configuration");
                _isRecording = false;
                RecordingStateChanged?.Invoke(this, EventArgs.Empty);
            }

            attach = await AttachConfigAsync(config, cancellationToken);
            if (!string.Equals(attach.ConfigIdentity, ReplayBufferConfigIdentity.Serialize(config), StringComparison.Ordinal))
                throw new InvalidOperationException("Capture worker did not apply requested capture configuration.");
        }

        ApplyAttachState(attach, config);
        if (_isRecording) return;
        var start = await SendAsync<CaptureWorkerAck>("start", new { }, cancellationToken);
        EnsureAccepted(start, "start capture");
        _isRecording = true;
        RecordingStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryEnsureAttachedAsync(cancellationToken)) return;
        await SendAsync<CaptureWorkerAck>("stop", new { }, cancellationToken);
        _isRecording = false;
        RecordingStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null)
    {
        await EnsureAttachedAsync(cancellationToken);
        var result = await SendAsync<CaptureWorkerSaveResult>("save", new CaptureWorkerSaveRequest(outputFolder, titleOverride, clipWindow), cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error)) throw new InvalidOperationException(result.Error);
        await SendAsync<CaptureWorkerAck>("ack-save", new { result.Path }, cancellationToken);
        return result.Path;
    }

    public void SetCapturePaused(bool paused)
        => _ = SendBestEffortAsync("pause", new { paused });

    public void RequestFrameRate(int frameRate)
        => _ = SendBestEffortAsync("frame-rate", new { frameRate });

    public async Task ShutdownWorkerAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryEnsureAttachedAsync(cancellationToken)) return;
        await SendAsync<CaptureWorkerAck>("shutdown", new { }, cancellationToken);
        _isRecording = false;
        Disconnect();
    }

    public async Task UpdateHotkeyAsync(string hotkey, CancellationToken cancellationToken = default)
    {
        await EnsureAttachedAsync(cancellationToken);
        await SendAsync<CaptureWorkerAck>("hotkey", new { hotkey }, cancellationToken);
    }

    public void Dispose()
    {
        _disposed = true;
        Disconnect();
        _connectionGate.Dispose();
        _writeGate.Dispose();
    }

    private async Task EnsureAttachedAsync(CancellationToken cancellationToken)
    {
        if (_pipe?.IsConnected == true) return;
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_pipe?.IsConnected == true) return;
            StartWorkerProcessIfNeeded();
            _pipe = CaptureWorkerPipe.CreateClient();
            await _pipe.ConnectAsync(5000, cancellationToken);
            _reader = Task.Run(() => ReadLoopAsync(_pipe));
            await SendAsync<CaptureWorkerHandshake>("handshake", new { ClientId = Environment.ProcessId }, cancellationToken);
            var config = _configProvider();
            var attach = await AttachConfigAsync(config, cancellationToken);
            ApplyAttachState(attach, config);
            foreach (var save in attach.UnacknowledgedSaves)
            {
                SaveCompleted?.Invoke(this, new ReplaySaveCompleted(save.Path, save.Title, save.CompletedUtc, save.Error));
                await SendAsync<CaptureWorkerAck>("ack-save", new { save.Path }, cancellationToken);
            }
        }
        finally { _connectionGate.Release(); }
    }

    private async Task<CaptureWorkerAttachResponse> AttachConfigAsync(ReplayBufferConfig config, CancellationToken cancellationToken)
        => await SendAsync<CaptureWorkerAttachResponse>("attach", config, cancellationToken);

    private void ApplyAttachState(CaptureWorkerAttachResponse attach, ReplayBufferConfig config)
    {
        _isRecording = attach.Recording;
        _durationSeconds = config.DurationSeconds;
        RecordingStateChanged?.Invoke(this, EventArgs.Empty);
        PublishHealth(attach.Health);
    }

    private static void EnsureAccepted(CaptureWorkerAck ack, string operation)
    {
        if (!ack.Accepted) throw new InvalidOperationException($"Capture worker failed to {operation}: {ack.Error}");
    }

    private async Task<bool> TryEnsureAttachedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureAttachedAsync(cancellationToken);
            return true;
        }
        catch (Exception error)
        {
            AppLog.Info($"Capture worker unavailable: {error.Message}");
            Disconnect();
            return false;
        }
    }

    private async Task<T> SendAsync<T>(string type, object payload, CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new IOException("Capture worker pipe is not connected.");
        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[requestId] = completion;
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CaptureWorkerPipe.WriteAsync(pipe, type, requestId, payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            var result = await completion.Task.WaitAsync(cancellationToken);
            return result.Deserialize<T>() ?? throw new InvalidDataException($"Capture worker returned invalid {type} response.");
        }
        finally
        {
            lock (_pending) _pending.Remove(requestId);
        }
    }

    private async Task SendBestEffortAsync(string type, object payload)
    {
        if (_disposed) return;
        try
        {
            if (!await TryEnsureAttachedAsync(CancellationToken.None)) return;
            await SendAsync<CaptureWorkerAck>(type, payload, CancellationToken.None);
        }
        catch (Exception error) { AppLog.Info($"Capture worker {type} failed: {error.Message}"); }
    }

    private async Task ReadLoopAsync(Stream pipe)
    {
        try
        {
            while (pipe is { CanRead: true })
            {
                var message = await CaptureWorkerPipe.ReadAsync(pipe, CancellationToken.None);
                if (message is null) break;
                if (message.Type == "response")
                {
                    TaskCompletionSource<JsonElement>? completion;
                    lock (_pending) _pending.TryGetValue(message.RequestId, out completion);
                    completion?.TrySetResult(message.Payload);
                    continue;
                }

                switch (message.Type)
                {
                    case "health":
                        var health = message.Payload.Deserialize<ReplayCaptureHealth>();
                        if (health is not null) Dispatcher.UIThread.Post(() => PublishHealth(health));
                        break;
                    case "recording-stopped":
                        Dispatcher.UIThread.Post(() =>
                        {
                            _isRecording = false;
                            RecordingStateChanged?.Invoke(this, EventArgs.Empty);
                            RecordingStopped?.Invoke(this, EventArgs.Empty);
                        });
                        break;
                    case "save-started":
                        Dispatcher.UIThread.Post(() => SaveStarted?.Invoke(this, EventArgs.Empty));
                        break;
                    case "save-completed":
                        var completed = message.Payload.Deserialize<CaptureWorkerSaveResult>();
                        if (completed is not null)
                        {
                            Dispatcher.UIThread.Post(() => SaveCompleted?.Invoke(this, new ReplaySaveCompleted(completed.Path, completed.Title, completed.CompletedUtc, completed.Error)));
                            _ = SendBestEffortAsync("ack-save", new { completed.Path });
                        }
                        break;
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidDataException)
        {
            AppLog.Info($"Capture worker connection lost: {error.Message}");
        }
        finally
        {
            lock (_pending)
            {
                foreach (var pending in _pending.Values) pending.TrySetException(new IOException("Capture worker connection closed."));
            }
            if (ReferenceEquals(_pipe, pipe)) _pipe = null;
        }
    }

    private void PublishHealth(ReplayCaptureHealth? health)
    {
        if (health is null) return;
        _health = health;
        HealthChanged?.Invoke(this, health);
    }

    private void StartWorkerProcessIfNeeded()
    {
        if (_process is { HasExited: false }) return;
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("ClypDat process path unavailable.");
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--capture-worker");
        _process = Process.Start(info) ?? throw new InvalidOperationException("Capture worker did not start.");
    }

    private void Disconnect()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _reader = null;
    }
}
