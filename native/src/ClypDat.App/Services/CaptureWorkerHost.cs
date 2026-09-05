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

    // The worker outlives the app, so this list is bounded rather than
    // session-long: a client that has been away for 32 saves is recovering
    // from the library folder anyway.
    internal const int MaximumUnacknowledgedSaves = 32;
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static readonly SemaphoreSlim FullSessionToggleGate = new(1, 1);
    private static readonly StorageProtectionService Storage = new();
    private static readonly AutoClipPackStore AutoClipPacks = new();
    private static readonly CancellationTokenSource Shutdown = new();
    private static NamedPipeServerStream? _client;
    private static ReplayBufferConfig? _config;
    private static IReplayBuffer? _buffer;
    private static GlobalHotkeyService? _hotkey;
    private static GlobalHotkeyService? _fullSessionHotkey;
    private static string? _clipGameName;
    private static DetectorHostClient? _detectorHost;
    private static AutoClipPackSelection? _detectorPack;
    private static IDetectorFrameSource? _detectorFrameSource;
    private static bool _autoClipDetectionEnabled;

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
            _fullSessionHotkey?.Dispose();
            _buffer?.Dispose();
            _detectorHost?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
                case "full-session-hotkey":
                    SetFullSessionHotkey(message.Payload.GetProperty("hotkey").GetString() ?? string.Empty);
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    break;
                case "clip-game-name":
                    _clipGameName = message.Payload.GetProperty("gameDisplayName").GetString();
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    break;
                case "auto-clip-policy":
                    await ReplyAsync(client, message, await ApplyAutoClipPolicyAsync(message.Payload, cancellationToken), cancellationToken);
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
                    var acknowledgedId = message.Payload.TryGetProperty("saveId", out var saveIdElement) && saveIdElement.TryGetGuid(out var parsedId)
                        ? parsedId
                        : Guid.Empty;
                    var acknowledgedPath = message.Payload.TryGetProperty("path", out var path) ? path.GetString() : null;
                    RemoveAcknowledgedSaves(UnacknowledgedSaves, acknowledgedId, acknowledgedPath);
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    break;
                case "shutdown":
                    await ReplyAsync(client, message, new CaptureWorkerAck(true), cancellationToken);
                    Shutdown.Cancel();
                    if (_buffer is not null) await _buffer.StopAsync(CancellationToken.None);
                    // Returning here ends the worker process, which kills any
                    // ffmpeg still muxing a session's audio - losing it for
                    // good. Nothing awaited that task before.
                    if (_buffer is IFullSessionFinalizeReporter pending)
                        await pending.WaitForBackgroundFinalizeAsync(TimeSpan.FromMinutes(5));
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
        var configChanged = !string.Equals(ConfigIdentity(_config), ConfigIdentity(config), StringComparison.Ordinal);
        if (_buffer is not null && configChanged && !_buffer.IsRecording)
        {
            _buffer.Dispose();
            _buffer = null;
        }

        if (_buffer is null)
        {
            _config = config;
            _buffer = ReplayBufferFactory.CreateLocal(() => _config!);
            _buffer.RecordingStopped += (_, _) => _ = SendEventAsync("recording-stopped", new { });
            if (_buffer is IFullSessionFinalizeReporter finalizes)
                finalizes.FullSessionFinalizeChanged += (_, active) => _ = SendEventAsync("full-session-finalize", active);
            AttachDetectorFrameSource(_buffer);
            if (_buffer is IReplayCaptureDiagnostics diagnostics)
                diagnostics.HealthChanged += (_, health) => _ = SendEventAsync("health", health with { Storage = Storage.Health });
        }
        else if (!configChanged)
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
        SetFullSessionHotkey(config.FullSessionHotkey);
        var response = new CaptureWorkerAttachResponse(
            _buffer?.IsRecording == true,
            ConfigIdentity(_config),
            GetHealth(),
            DrainUnacknowledgedSaves(UnacknowledgedSaves),
            NativeReplayBuffer.ActiveFinalizeSnapshot());
        await ReplyAsync(client, message, response, cancellationToken);
    }

    private static async Task EnsureBufferAsync()
    {
        if (_buffer is null)
        {
            if (_config is null) throw new InvalidOperationException("Worker is not attached.");
            _buffer = ReplayBufferFactory.CreateLocal(() => _config!);
            AttachDetectorFrameSource(_buffer);
        }
    }

    internal static int RemoveAcknowledgedSaves(List<CaptureWorkerSaveResult> backlog, Guid saveId, string? path)
    {
        lock (backlog)
        {
            return backlog.RemoveAll(item =>
                (saveId != Guid.Empty && item.SaveId == saveId) ||
                (!string.IsNullOrWhiteSpace(path) && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>
    /// Takes the backlog for an attaching client and empties it in the same
    /// step. Delivery is the acknowledgement: the explicit ack that used to
    /// clear this is sent over a connection that is frequently torn down at
    /// exactly this moment - a restart spawns a redundant worker which exits
    /// because the pipe is already owned, and the resulting recovery drops
    /// every ack - which left the same saves replaying on every restart.
    /// Nothing is lost by clearing early: startup runs a full library
    /// reconciliation over the clips folder regardless.
    /// </summary>
    internal static IReadOnlyList<CaptureWorkerSaveResult> DrainUnacknowledgedSaves(List<CaptureWorkerSaveResult> backlog)
    {
        lock (backlog)
        {
            if (backlog.Count == 0) return Array.Empty<CaptureWorkerSaveResult>();
            var drained = backlog.ToArray();
            backlog.Clear();
            return drained;
        }
    }

    internal static void RememberUnacknowledgedSave(List<CaptureWorkerSaveResult> backlog, CaptureWorkerSaveResult save)
    {
        lock (backlog)
        {
            backlog.Add(save);
            if (backlog.Count > MaximumUnacknowledgedSaves)
                backlog.RemoveRange(0, backlog.Count - MaximumUnacknowledgedSaves);
        }
    }

    private static async Task<CaptureWorkerAck> ApplyAutoClipPolicyAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        try
        {
            var gameId = payload.TryGetProperty("gameId", out var game) ? game.GetString() : null;
            var enabled = payload.TryGetProperty("enabled", out var value) && value.GetBoolean()
                          && string.Equals(gameId, "helldivers2", StringComparison.OrdinalIgnoreCase);
            var eventIds = payload.TryGetProperty("enabledEventIds", out var events) && events.ValueKind == JsonValueKind.Array
                ? events.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray()
                : Array.Empty<string>();

            _autoClipDetectionEnabled = enabled;
            if (!enabled)
            {
                if (_buffer is not null) AttachDetectorFrameSource(_buffer);
                if (_detectorHost is not null)
                {
                    await _detectorHost.DisposeAsync();
                    _detectorHost = null;
                }
                _detectorPack = null;
                return new CaptureWorkerAck(true);
            }

            _detectorPack ??= AutoClipPacks.Resolve("helldivers2");
            _detectorHost ??= CreateDetectorHost();
            await _detectorHost.StartAsync(
                new DetectorHostPolicy(
                    "helldivers2",
                    eventIds,
                    _detectorPack.PackId,
                    _detectorPack.Version,
                    _detectorPack.Hash),
                cancellationToken);
            if (_buffer is not null) AttachDetectorFrameSource(_buffer);
            CaptureWorkerLog.Info($"Helldivers detector policy: enabled={enabled}, events={string.Join(',', eventIds)}.");
            return new CaptureWorkerAck(true);
        }
        catch (Exception error)
        {
            _autoClipDetectionEnabled = false;
            CaptureWorkerLog.Error("Helldivers detector could not start.", error);
            return new CaptureWorkerAck(false, error.Message);
        }
    }

    private static DetectorHostClient CreateDetectorHost()
    {
        var host = new DetectorHostClient();
        host.Detected += (_, detected) => _ = SendEventAsync("auto-clip-detected", detected);
        host.StatusChanged += (_, status) =>
        {
            CaptureWorkerLog.Info($"Detector host status: {status}.");
            _ = SendEventAsync("auto-clip-status", new { gameId = "helldivers2", status });
        };
        host.Quarantined += (_, reason) =>
        {
            _autoClipDetectionEnabled = false;
            if (_buffer is not null) AttachDetectorFrameSource(_buffer);
            if (_detectorPack is not null) AutoClipPacks.Quarantine(_detectorPack);
            _detectorPack = null;
            CaptureWorkerLog.Error($"Detector host quarantined: {reason}");
            _ = SendEventAsync("auto-clip-status", new { gameId = "helldivers2", status = $"Failed — {reason}" });
        };
        return host;
    }

    private static void AttachDetectorFrameSource(IReplayBuffer buffer)
    {
        var desired = _autoClipDetectionEnabled ? buffer as IDetectorFrameSource : null;
        if (ReferenceEquals(_detectorFrameSource, desired)) return;
        if (_detectorFrameSource is not null)
            _detectorFrameSource.DetectorFrameAvailable -= DetectorFrameAvailable;
        _detectorFrameSource = desired;
        if (_detectorFrameSource is not null)
            _detectorFrameSource.DetectorFrameAvailable += DetectorFrameAvailable;
    }

    private static void DetectorFrameAvailable(object? sender, DetectorFrameSnapshot frame) => _detectorHost?.Offer(frame);

    private static async Task<CaptureWorkerSaveResult> SaveAsync(CaptureWorkerSaveRequest request, CancellationToken cancellationToken)
    {
        var saveId = request.SaveId.GetValueOrDefault();
        if (saveId == Guid.Empty) saveId = Guid.NewGuid();
        var requestedUtc = request.RequestedUtc ?? DateTime.UtcNow;
        await SendEventAsync("save-started", new ReplaySaveStarted(saveId, requestedUtc));
        if (!await SaveGate.WaitAsync(0, cancellationToken))
        {
            var busy = new CaptureWorkerSaveResult(string.Empty, request.TitleOverride, DateTime.UtcNow, "A replay save is already in progress.", saveId, requestedUtc);
            await SendEventAsync("save-failed", busy);
            return busy;
        }
        try
        {
            await EnsureBufferAsync();
            if (!Storage.CanSave(_config?.BitrateMbps ?? 15, TimeSpan.FromSeconds(_config?.DurationSeconds ?? 60), out var storageReason))
            {
                var unavailable = new CaptureWorkerSaveResult(string.Empty, request.TitleOverride, DateTime.UtcNow, storageReason, saveId, requestedUtc);
                await SendEventAsync("save-failed", unavailable);
                return unavailable;
            }
            var stopwatch = Stopwatch.StartNew();
            var gameDisplayName = string.IsNullOrWhiteSpace(request.GameDisplayNameOverride) ? _config?.GameDisplayName : request.GameDisplayNameOverride;
            var path = await _buffer!.SaveReplayAsync(request.OutputFolder, cancellationToken, request.TitleOverride, request.ClipWindow, gameDisplayName, saveId);
            stopwatch.Stop();
            Storage.RecordWrite(path, stopwatch.Elapsed);
            if (_config is not null)
            {
                ClipInfoSidecar.Save(_config.LibraryFolder, path, new ClipInfo(
                    gameDisplayName,
                    null,
                    request.TitleOverride ?? gameDisplayName,
                    File.GetCreationTimeUtc(path),
                    CaptureSource: _config.CaptureSource));
            }
            var result = new CaptureWorkerSaveResult(path, request.TitleOverride, DateTime.UtcNow, null, saveId, requestedUtc);
            RememberUnacknowledgedSave(UnacknowledgedSaves, result);
            await SendEventAsync("save-completed", result);
            return result;
        }
        catch (Exception error)
        {
            CaptureWorkerLog.Error("Replay clip save failed.", error);
            var result = new CaptureWorkerSaveResult(string.Empty, request.TitleOverride, DateTime.UtcNow, error.Message, saveId, requestedUtc);
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

    private static void SetFullSessionHotkey(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return;
        _fullSessionHotkey ??= new GlobalHotkeyService();
        _fullSessionHotkey.SetHotkey(hotkey);
        _fullSessionHotkey.Pressed -= FullSessionHotkeyPressed;
        _fullSessionHotkey.Pressed += FullSessionHotkeyPressed;
        _fullSessionHotkey.Start();
    }

    private static void FullSessionHotkeyPressed(object? sender, ReplayHotkeyPressedEventArgs args) =>
        _ = ToggleFullSessionRecordingAsync();

    private static async Task ToggleFullSessionRecordingAsync()
    {
        if (_buffer?.IsRecording != true || _config is null) return;
        await FullSessionToggleGate.WaitAsync().ConfigureAwait(false);
        await SaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_buffer?.IsRecording != true || _config is null) return;
            var enabled = !_config.FullSessionRecordingEnabled;
            await _buffer.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _config = _config with { FullSessionRecordingEnabled = enabled };
            await _buffer.StartAsync(CancellationToken.None).ConfigureAwait(false);
            CaptureWorkerLog.Info($"Full session recording toggled {(enabled ? "on" : "off")} by hotkey.");
            await SendEventAsync("full-session-toggled", new { enabled }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            CaptureWorkerLog.Error("Full session hotkey toggle failed.", error);
        }
        finally
        {
            SaveGate.Release();
            FullSessionToggleGate.Release();
        }
    }

    private static void HotkeyPressed(object? sender, ReplayHotkeyPressedEventArgs args)
    {
        if (_buffer?.IsRecording != true || _config is null) return;
        var duration = TimeSpan.FromSeconds(Math.Clamp(_config.DurationSeconds, 30, 1200));
        var folder = LibraryLayout.ClipsRoot(_config.LibraryFolder);
        var gameDisplayName = _clipGameName ?? _config.GameDisplayName;
        var saveId = Guid.NewGuid();
        _ = SaveAsync(new CaptureWorkerSaveRequest(folder, null, new ReplayClipWindow(args.PressedAtUtc - duration, args.PressedAtUtc), gameDisplayName, saveId, args.PressedAtUtc), CancellationToken.None);
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
        }
        catch (Exception error) { CaptureWorkerLog.Error("Worker priority setup failed.", error); }
    }
}

internal sealed record CaptureWorkerSaveRequest(
    string OutputFolder,
    string? TitleOverride,
    ReplayClipWindow? ClipWindow,
    string? GameDisplayNameOverride = null,
    Guid? SaveId = null,
    DateTime? RequestedUtc = null);

internal static class CaptureWorkerLog
{
    private static readonly object Sync = new();
    private static string Path => System.IO.Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "capture-worker.log");
    public static void Info(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTime.UtcNow:o} {message}\n");
            }
        }
        catch { }
    }
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
