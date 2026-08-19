using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal static class CaptureWorkerHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private static readonly List<CaptureWorkerSaveResult> UnacknowledgedSaves = new();
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static readonly StorageProtectionService Storage = new();
    private static readonly CancellationTokenSource Shutdown = new();
    private static NamedPipeServerStream? _client;
    private static ReplayBufferConfig? _config;
    private static IReplayBuffer? _buffer;
    private static GlobalHotkeyService? _hotkey;

    public static int Run()
    {
        if (!OperatingSystem.IsWindows()) return 2;
        Storage.HealthChanged += (_, _) => _ = SendEventAsync("health", GetHealth());
        using var mutex = new Mutex(true, CaptureWorkerProtocol.MutexName, out var created);
        if (!created) return 0;

        ApplyWorkerPriority();
        try
        {
            RunLoopAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            CaptureWorkerLog.Error("Worker stopped unexpectedly.", error);
            return 1;
        }
        finally
        {
            _hotkey?.Dispose();
            _buffer?.Dispose();
            Storage.Dispose();
        }
    }

    private static async Task RunLoopAsync()
    {
        while (!Shutdown.IsCancellationRequested)
        {
            using var server = CaptureWorkerPipe.CreateServer();
            await server.WaitForConnectionAsync(Shutdown.Token);
            _client = server;
            try
            {
                await ClientLoopAsync(server, Shutdown.Token);
            }
            catch (EndOfStreamException) { }
            catch (IOException) { }
            catch (OperationCanceledException) when (Shutdown.IsCancellationRequested) { }
            catch (Exception error) { CaptureWorkerLog.Error("Worker client loop failed.", error); }
            finally
            {
                if (ReferenceEquals(_client, server)) _client = null;
            }
        }
    }

    private static async Task ClientLoopAsync(Stream client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await CaptureWorkerPipe.ReadAsync(client, cancellationToken);
            if (message is null) return;
            switch (message.Type)
            {
                case "handshake":
                    await ReplyAsync(client, message, new CaptureWorkerHandshake(CaptureWorkerProtocol.Version, "capture-worker"), cancellationToken);
                    break;
                case "attach":
                    await AttachAsync(client, message, cancellationToken);
                    break;
                case "start":
                    await EnsureBufferAsync();
                    await _buffer!.StartAsync(cancellationToken);
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    break;
                case "stop":
                    if (_buffer is not null) await _buffer.StopAsync(cancellationToken);
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    break;
                case "pause":
                    if (_buffer is not null && message.Payload.TryGetProperty("paused", out var paused)) _buffer.SetCapturePaused(paused.GetBoolean());
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    break;
                case "frame-rate":
                    if (_buffer is IAdaptiveCaptureFrameRate adaptive && message.Payload.TryGetProperty("frameRate", out var frameRate)) adaptive.RequestFrameRate(frameRate.GetInt32());
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    break;
                case "hotkey":
                    SetHotkey(message.Payload.GetProperty("hotkey").GetString() ?? string.Empty);
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    break;
                case "health":
                    await ReplyAsync(client, message, GetHealth(), cancellationToken);
                    break;
                case "save":
                    var request = message.Payload.Deserialize<CaptureWorkerSaveRequest>(JsonOptions) ?? throw new InvalidDataException("Invalid save request.");
                    var result = await SaveAsync(request, cancellationToken);
                    await ReplyAsync(client, message, result, cancellationToken);
                    break;
                case "ack-save":
                    if (message.Payload.TryGetProperty("path", out var path)) UnacknowledgedSaves.RemoveAll(item => string.Equals(item.Path, path.GetString(), StringComparison.OrdinalIgnoreCase));
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    break;
                case "shutdown":
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    Shutdown.Cancel();
                    if (_buffer is not null) await _buffer.StopAsync(CancellationToken.None);
                    return;
                default:
                    await ReplyAsync(client, message, new CaptureWorkerAck(false, $"Unknown command '{message.Type}'."), cancellationToken);
                    break;
            }
        }
    }

    private static async Task AttachAsync(Stream client, CaptureWorkerEnvelope message, CancellationToken cancellationToken)
    {
        var config = message.Payload.Deserialize<ReplayBufferConfig>(JsonOptions) ?? throw new InvalidDataException("Invalid replay configuration.");
        if (_buffer is null || !string.Equals(ConfigIdentity(_config), ConfigIdentity(config), StringComparison.Ordinal))
        {
            if (_buffer is { IsRecording: false }) _buffer.Dispose();
            if (_buffer is null || !_buffer.IsRecording)
            {
                _config = config;
                _buffer?.Dispose();
                _buffer = ReplayBufferFactory.CreateLocal(() => _config!);
                _buffer.RecordingStopped += (_, _) => _ = SendEventAsync("recording-stopped", new { });
                if (_buffer is IReplayCaptureDiagnostics diagnostics)
                    diagnostics.HealthChanged += (_, health) => _ = SendEventAsync("health", health with { Storage = Storage.Health });
            }
        }
        else
        {
            _config = config;
        }

        ApplyWorkerPriority();
        Storage.Start(new[]
        {
            (config.LibraryFolder, "library"),
            (Path.GetTempPath(), "system-temp"),
            (config.FullSessionRecordingFolder, "full-session")
        });
        SetHotkey(config.SaveReplayHotkey);
        var response = new CaptureWorkerAttachResponse(
            _buffer?.IsRecording == true,
            ConfigIdentity(_config),
            GetHealth(),
            UnacknowledgedSaves.ToArray());
        await ReplyAsync(client, message, response, cancellationToken);
    }

    private static async Task EnsureBufferAsync()
    {
        if (_buffer is null)
        {
            if (_config is null) throw new InvalidOperationException("Worker is not attached.");
            _buffer = ReplayBufferFactory.CreateLocal(() => _config!);
        }
    }

    private static async Task<CaptureWorkerSaveResult> SaveAsync(CaptureWorkerSaveRequest request, CancellationToken cancellationToken)
    {
        if (!await SaveGate.WaitAsync(0, cancellationToken))
            return new CaptureWorkerSaveResult(string.Empty, request.TitleOverride, DateTime.UtcNow, "A replay save is already in progress.");
        try
        {
            await EnsureBufferAsync();
            if (!Storage.CanSave(_config?.BitrateMbps ?? 15, TimeSpan.FromSeconds(_config?.DurationSeconds ?? 60), out var storageReason))
                return new CaptureWorkerSaveResult(string.Empty, request.TitleOverride, DateTime.UtcNow, storageReason);
            await SendEventAsync("save-started", new { });
            var stopwatch = Stopwatch.StartNew();
            var path = await _buffer!.SaveReplayAsync(request.OutputFolder, cancellationToken, request.TitleOverride, request.ClipWindow);
            stopwatch.Stop();
            Storage.RecordWrite(path, stopwatch.Elapsed);
            if (_config is not null)
            {
                ClipInfoSidecar.Save(_config.LibraryFolder, path, new ClipInfo(
                    _config.GameDisplayName,
                    null,
                    request.TitleOverride ?? _config.GameDisplayName,
                    File.GetCreationTimeUtc(path),
                    CaptureSource: _config.CaptureSource));
            }
            var result = new CaptureWorkerSaveResult(path, request.TitleOverride, DateTime.UtcNow);
            UnacknowledgedSaves.Add(result);
            await SendEventAsync("save-completed", result);
            return result;
        }
        catch (Exception error)
        {
            var result = new CaptureWorkerSaveResult(string.Empty, request.TitleOverride, DateTime.UtcNow, error.Message);
            await SendEventAsync("save-failed", result);
            return result;
        }
        finally { SaveGate.Release(); }
    }

    private static void SetHotkey(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return;
        _hotkey ??= new GlobalHotkeyService();
        _hotkey.SetHotkey(hotkey);
        _hotkey.Pressed -= HotkeyPressed;
        _hotkey.Pressed += HotkeyPressed;
        _hotkey.Start();
    }

    private static void HotkeyPressed(object? sender, ReplayHotkeyPressedEventArgs args)
    {
        if (_buffer?.IsRecording != true || _config is null) return;
        var duration = TimeSpan.FromSeconds(Math.Clamp(_config.DurationSeconds, 30, 1200));
        var folder = LibraryLayout.ClipsRoot(_config.LibraryFolder);
        _ = SaveAsync(new CaptureWorkerSaveRequest(folder, null, new ReplayClipWindow(args.PressedAtUtc - duration, args.PressedAtUtc)), CancellationToken.None);
    }

    private static ReplayCaptureHealth GetHealth()
    {
        var health = _buffer is IReplayCaptureDiagnostics diagnostics ? diagnostics.GetHealthSnapshot() : ReplayCaptureHealth.Unknown("Worker");
        return health with { Storage = Storage.Health };
    }

    private static string ConfigIdentity(ReplayBufferConfig? config)
        => ReplayBufferConfigIdentity.Serialize(config);

    private static async Task ReplyAsync(Stream client, CaptureWorkerEnvelope request, object payload, CancellationToken cancellationToken)
    {
        await SendAsync(client, "response", request.RequestId, payload, cancellationToken);
    }

    private static async Task SendEventAsync(string type, object payload)
    {
        var client = _client;
        if (client is null) return;
        try { await SendAsync(client, type, Guid.Empty, payload, CancellationToken.None); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task SendAsync(Stream stream, string type, Guid requestId, object payload, CancellationToken cancellationToken)
    {
        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CaptureWorkerPipe.WriteAsync(stream, type, requestId, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private static void ApplyWorkerPriority()
    {
        try
        {
            if (_config is not null) ProcessPriorityService.Apply(_config.ProcessPriority);
            GpuScheduling.TryRaiseProcessGpuPriority();
        }
        catch (Exception error) { CaptureWorkerLog.Error("Worker priority setup failed.", error); }
    }
}

internal sealed record CaptureWorkerSaveRequest(string OutputFolder, string? TitleOverride, ReplayClipWindow? ClipWindow);

internal static class CaptureWorkerLog
{
    private static readonly object Sync = new();
    private static string Path => System.IO.Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "capture-worker.log");
    public static void Error(string message, Exception? error = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTime.UtcNow:o} {message} {error}\n");
            }
        }
        catch { }
    }
}
