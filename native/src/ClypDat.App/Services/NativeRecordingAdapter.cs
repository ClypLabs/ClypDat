using System.Diagnostics;
using System.Text.Json;
using ClypDat.Capture.Abstractions;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>Managed control and publication boundary. Capture, pacing, audio,
/// encoding, media history, save assembly, and composition run in RecorderCore.</summary>
internal sealed class NativeRecordingAdapter : IReplayBuffer, IReplayCaptureDiagnostics,
    IAdaptiveCaptureFrameRate, IDetectorFrameSource, IFullSessionRecorderLifecycle, IVideoOverlaySettingsReceiver
{
    private readonly Func<ReplayBufferConfig> _configuration;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private NativeRecorderSession? _session;
    private ReplayBufferConfig? _active;
    private OverlayCaptureSettings _overlays = OverlayCaptureSettings.None;
    private ulong _overlayRevision;
    private DetectorRegionSet? _regions;
    private CancellationTokenSource? _controlCancellation;
    private Task _controlTask = Task.CompletedTask, _lastStop = Task.CompletedTask;
    private ReplayCaptureHealth _health = ReplayCaptureHealth.Unknown("Native C++");
    private int _saving;
    private long _lastNativeDiagnosticLogTicks;
    private string? _lastLoggedCaptureSource;
    private long _lastLoggedSourceRecoveries = -1;
    private bool _recording;
    private volatile bool _lastFrozen;
    private string _fullSessionPath = "";
    private readonly HashSet<long> _closedSessions = [];
    private readonly HashSet<string> _publishedSessions = new(StringComparer.OrdinalIgnoreCase);
    public NativeRecordingAdapter(Func<ReplayBufferConfig> configuration) => _configuration = configuration;
    public bool IsRecording { get { lock (_gate) return _recording; } }
    public TimeSpan Duration => TimeSpan.FromSeconds((_active ?? _configuration()).DurationSeconds);
    public bool LastSaveVideoWasFrozen => _lastFrozen;
    public event EventHandler? RecordingStopped;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;
    public event EventHandler<DetectorFrameSnapshot>? DetectorFrameAvailable;
    public event EventHandler<string>? FullSessionClosed;
    public ReplayCaptureHealth GetHealthSnapshot() { lock (_gate) return _health; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRecording) return;
            NativeRecorderSession? previous;
            lock (_gate) { previous = _session; _session = null; }
            if (previous is not null)
            {
                _controlCancellation?.Cancel();
                try { await _controlTask.ConfigureAwait(false); }
                catch (Exception error) { AppLog.Error("Native recording control task failed before restart.", error); }
                try { await Task.Run(previous.Stop, CancellationToken.None).ConfigureAwait(false); }
                finally { previous.Dispose(); }
                _controlCancellation?.Dispose(); _controlCancellation = null;
            }
            NativeRecorderSession.RequireCompleteEngine();
            var configuration = _configuration();
            var root = Path.Combine(AppDataPaths.Root, "native-replay-buffer", "session-" + Guid.NewGuid().ToString("N"));
            var fullSessionPath = FullSessionPath(configuration);
            var session = NativeRecorderSession.Create(configuration, root, fullSessionPath);
            var artwork = new RecordingKeyboardArtworkService();
            try
            {
                OverlayCaptureSettings settings;
                lock (_gate) { _active = configuration; settings = _overlays; _fullSessionPath = fullSessionPath; _closedSessions.Clear(); _publishedSessions.Clear(); }
                session.UpdateOverlay(settings, ++_overlayRevision, NowUs());
                var initialArtwork = artwork.Update(settings, [], 0, Math.Max(2, configuration.CaptureWidth), Math.Max(2, configuration.CaptureHeight), NowUs());
                if (initialArtwork is not null) session.UpdateArtwork(initialArtwork);
                session.Start();
                GpuScheduling.TryRaiseProcessGpuPriority();
                var startup = Stopwatch.StartNew();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var state = session.ReadHealth();
                    PublishHealth(configuration, state.Value, state.Details);
                    var error = Text(state.Details, "error");
                    if (state.Value.RestartRequired != 0 || error.Length > 0) throw new InvalidOperationException(error.Length > 0 ? error : "Native recorder requires a worker restart.");
                    if (state.Value.Encoded > 0) break;
                    if (startup.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Native recorder did not deliver an encoded frame within 30 seconds.");
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
                lock (_gate)
                {
                    if (!Equals(_overlays, settings))
                    {
                        _overlays = _overlays with { RecordingMode = settings.RecordingMode };
                        session.UpdateOverlay(_overlays, ++_overlayRevision, NowUs());
                    }
                    ConfigureDetector(session, _regions, configuration);
                    _session = session; _recording = true;
                }
                var cancellation = _controlCancellation = new CancellationTokenSource();
                _controlTask = Task.Run(() => Control(session, configuration, artwork, cancellation.Token));
            }
            catch
            {
                lock (_gate) { if (ReferenceEquals(_session, session)) { _session = null; _recording = false; } }
                try { await Task.Run(session.Stop, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { AppLog.Error("Native recording startup cleanup requires a worker restart.", error); }
                artwork.Dispose(); session.Dispose(); throw;
            }
        }
        finally { _lifecycle.Release(); }
    }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        var stop = StopCoreAsync(cancellationToken); lock (_gate) _lastStop = stop; return stop;
    }
    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NativeRecorderSession? session;
            lock (_gate) { session = _session; _session = null; _recording = false; }
            _controlCancellation?.Cancel();
            try { await _controlTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { AppLog.Error("Native recording control task failed before shutdown.", error); }
            _controlCancellation?.Dispose(); _controlCancellation = null;
            if (session is null) return;
            try
            {
                await Task.Run(session.Stop, CancellationToken.None).ConfigureAwait(false);
                var state = session.ReadHealth(); PublishHealth(_active!, state.Value, state.Details);
            }
            finally { session.Dispose(); RecordingStopped?.Invoke(this, EventArgs.Empty); }
        }
        finally { _lifecycle.Release(); }
    }
    public Task WaitForFullSessionCloseAsync(TimeSpan timeout) { lock (_gate) return _lastStop.WaitAsync(timeout); }
    public void SetCapturePaused(bool paused) { lock (_gate) _session?.Pause(paused); }
    public void RequestFrameRate(int frameRate) { lock (_gate) _session?.FrameRate(frameRate); }
    public void SetVideoOverlaySettings(OverlayCaptureSettings settings)
    {
        lock (_gate)
        {
            if (settings.Revision > 0 && settings.Revision <= _overlays.Revision) return;
            if (_session is not null) settings = settings with { RecordingMode = _overlays.RecordingMode };
            _overlays = settings;
            _session?.UpdateOverlay(settings, ++_overlayRevision, ToUs(settings.AppliedAtUtc ?? MonotonicClock.UtcNow));
        }
    }
    public void SetDetectorRegions(DetectorRegionSet? regions)
    {
        lock (_gate) { _regions = regions; if (_session is not null && _active is not null) ConfigureDetector(_session, regions, _active); }
    }
    private static void ConfigureDetector(NativeRecorderSession session, DetectorRegionSet? regions, ReplayBufferConfig settings)
    {
        session.ConfigureDetector(regions);
    }
    private void Control(NativeRecorderSession session, ReplayBufferConfig configuration, RecordingKeyboardArtworkService artwork, CancellationToken cancellationToken)
    {
        using var ownedArtwork = artwork;
        ulong sequence = 0;
        var first = true;
        var previousDetector = DateTime.MinValue;
        var lastHealth = 0L;
        var outputWidth = Math.Max(2, configuration.CaptureWidth);
        var outputHeight = Math.Max(2, configuration.CaptureHeight);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var notification = session.WaitEvents(sequence); sequence = notification.Sequence;
                OverlayCaptureSettings settings;
                lock (_gate) settings = _overlays;
                var geometryChanged = false;
                var now = Stopwatch.GetTimestamp();
                if (first || (notification.Mask & 16) != 0 ||
                    (notification.Mask & 1) != 0 && now - lastHealth >= Stopwatch.Frequency / 4 || now - lastHealth >= Stopwatch.Frequency)
                {
                    var state = session.ReadHealth();
                    var width = (int)Number(state.Details, "outputWidth"); var height = (int)Number(state.Details, "outputHeight");
                    if (width > 0 && height > 0 && (width != outputWidth || height != outputHeight))
                    { outputWidth = width; outputHeight = height; geometryChanged = true; }
                    PublishHealth(configuration, state.Value, state.Details);
                    lastHealth = now;
                    if (state.Value.RestartRequired != 0 || state.Value.Running == 0)
                    {
                        lock (_gate) _recording = false;
                        RecordingStopped?.Invoke(this, EventArgs.Empty); return;
                    }
                }
                if (first || geometryChanged || (notification.Mask & (2 | 32)) != 0)
                {
                    var pixels = artwork.Update(settings, session.PressedKeys(), unchecked((long)sequence), outputWidth, outputHeight, NowUs());
                    if (pixels is not null) session.UpdateArtwork(pixels);
                }
                if (first || (notification.Mask & 4) != 0)
                {
                    DetectorRegionSet? regions; lock (_gate) regions = _regions;
                    if (regions is not null && session.ReadDetector() is { } detector && detector.CapturedUtc != previousDetector)
                    { previousDetector = detector.CapturedUtc; DetectorFrameAvailable?.Invoke(this, detector); }
                }
                first = false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            ReplayCaptureHealth failed;
            lock (_gate) { _recording = false; failed = _health = _health with { State = ReplayCaptureState.Failed, LastFailure = error.Message, UpdatedUtc = DateTime.UtcNow }; }
            HealthChanged?.Invoke(this, failed); RecordingStopped?.Invoke(this, EventArgs.Empty);
        }
    }
    private void PublishHealth(ReplayBufferConfig configuration, NativeRecorderSession.Health native, JsonElement details)
    {
        var error = Text(details, "error");
        var captureSource = Text(details, "source");
        var state = native.RestartRequired != 0 || error.Length > 0 ? ReplayCaptureState.Failed : native.Running == 0 ? ReplayCaptureState.Stopped :
            native.Encoded == 0 ? ReplayCaptureState.Starting : ReplayCaptureState.Healthy;
        var fullError = Text(details, "fullSessionError");
        var fullState = fullError.Length > 0 ? FullSessionState.Failed : Bool(details, "fullSessionRunning") ? FullSessionState.Recording :
            Bool(details, "fullSessionFinished") ? FullSessionState.Completed : configuration.FullSessionRecordingEnabled ? FullSessionState.Starting : FullSessionState.Off;
        var health = new ReplayCaptureHealth("Native C++", captureSource, state, (int)native.ActiveFps,
            Number(details, "inputFps"), Number(details, "uniqueFps"), Number(details, "outputFps"),
            (long)Number(details, "duplicates"), checked((long)native.Replaced), (int)native.QueueDepth, Text(details, "encoder"), Text(details, "adapter"), error, DateTime.UtcNow)
        {
            NativeEngineVersion = 3, EncodeQueueCapacity = (int)native.QueueCapacity, ConfiguredFrameRate = configuration.FrameRate,
            FrameRateMode = configuration.FrameRateMode, EncoderProfile = configuration.EncoderProfile, CapturePaused = native.Paused != 0,
            TotalDroppedFrames = checked((long)native.Replaced + (long)Number(details, "backpressureDrops")), EncodeQueueReplacements = checked((long)native.Replaced),
            SourceDeliveredFrameRate = Number(details, "wgcDeliveredFps"), SaveInProgress = Volatile.Read(ref _saving) != 0,
            StartupPhase = native.Encoded == 0 ? ReplayCaptureStartupPhase.OpeningEncoder : ReplayCaptureStartupPhase.Ready,
            PipelineRecoveryAction = native.RestartRequired != 0 ? ReplayPipelineRecoveryAction.RestartWorker : ReplayPipelineRecoveryAction.None,
            EncodeQueueAge = TimeSpan.FromMilliseconds(Number(details, "queueAgeMs")),
            SubmissionP95Ms = Number(details, "submissionP95Ms"),
            ProcessingMaxMs = Number(details, "processingMaxMs"), SubmissionMaxMs = Number(details, "submissionMaxMs"),
            ProcessingPath = Text(details, "processingPath"),
            TextureReadbackMs = Number(details, "textureReadbackMs"), VideoProcessorMs = Number(details, "videoProcessorMs"),
            SoftwareConvertMs = Number(details, "softwareConvertMs"), HardwareUploadMs = Number(details, "hardwareUploadMs"),
            OverlayComposeMs = Number(details, "overlayComposeMs"), GpuConversionFallbacks = (long)Number(details, "gpuConversionFallbacks"),
            GpuConversionFallbackError = Text(details, "gpuConversionFallbackError"),
            ProcessGpuPriority = GpuScheduling.ProcessPriorityApplied,
            EncoderOutputLatencyMaxMs = Number(details, "completionMaxMs"),
            EncoderOutputLatencyP95Ms = Number(details, "completionP95Ms"), SurfacesInUse = (int)Number(details, "surfacesInUse"),
            SurfaceCapacity = (int)Number(details, "surfaceCapacity"), EncoderInputPath = Bool(details, "hardwareInput") ? "D3D11" : "Software",
            FrameRateProtectionActive = Bool(details, "frameRateProtected"), AdapterDescription = Text(details, "adapter"), AdapterLuid = Text(details, "adapterLuid"),
            ProcessingGpuPriority = Bool(details, "gpuDevicePriorityApplied") ? (int)Number(details, "gpuDevicePriority") : null,
            HdrCompatibilityStatus = !configuration.ReplayHdrCompatibilityEnabled || !Bool(details, "displayProfileAvailable") ? ReplayHdrCompatibilityStatus.Unavailable :
                !Bool(details, "hdrDisplay") ? ReplayHdrCompatibilityStatus.SdrDisplay : Bool(details, "hdrConversion") ? ReplayHdrCompatibilityStatus.ConversionActive :
                native.Encoded == 0 ? ReplayHdrCompatibilityStatus.PreparingConversion : ReplayHdrCompatibilityStatus.ConversionFailed,
            WgcRequestedUpdateInterval = Bool(details, "updateIntervalAvailable") ? TimeSpan.FromTicks((long)Number(details, "requestedInterval100ns")) : null,
            WgcAppliedUpdateInterval = Bool(details, "updateIntervalAvailable") ? TimeSpan.FromTicks((long)Number(details, "appliedInterval100ns")) : null,
            FullSession = new(fullState, Text(details, "fullSessionPath") is { Length: > 0 } path ? path : _fullSessionPath, fullError)
        };
        lock (_gate) _health = health;
        HealthChanged?.Invoke(this, health);
        var sourceRecoveries = (long)Number(details, "sourceRecoveries");
        if (_lastLoggedCaptureSource is { } previousSource && previousSource != captureSource)
            AppLog.Info($"Capture backend changed from '{previousSource}' to '{captureSource}' after source recovery; sourceRecoveries={sourceRecoveries}.");
        else if (_lastLoggedSourceRecoveries >= 0 && sourceRecoveries > _lastLoggedSourceRecoveries)
            AppLog.Info($"Capture source recovered on '{captureSource}'; sourceRecoveries={sourceRecoveries}.");
        _lastLoggedCaptureSource = captureSource;
        _lastLoggedSourceRecoveries = sourceRecoveries;
        var diagnosticNow = Stopwatch.GetTimestamp();
        var previousDiagnostic = Volatile.Read(ref _lastNativeDiagnosticLogTicks);
        if (diagnosticNow - previousDiagnostic >= Stopwatch.Frequency &&
            Interlocked.CompareExchange(ref _lastNativeDiagnosticLogTicks, diagnosticNow, previousDiagnostic) == previousDiagnostic)
        {
            AppLog.Debug($"Native capture: source={captureSource}; input={health.InputFrameRate:F1} fresh={health.UniqueFrameRate:F1} output={health.OutputFrameRate:F1}fps; queue={health.QueueDepth}/{health.EncodeQueueCapacity}; dropped={health.TotalDroppedFrames}; processingMax={health.ProcessingMaxMs:F2}ms path={health.ProcessingPath}; readback={health.TextureReadbackMs:F2}ms sourceCursorComposition={Number(details, "sourceCursorCompositionMs"):F2}ms videoProcessor={health.VideoProcessorMs:F2}ms softwareConvert={health.SoftwareConvertMs:F2}ms upload={health.HardwareUploadMs:F2}ms overlay={health.OverlayComposeMs:F2}ms; GPU fallbacks={health.GpuConversionFallbacks} error='{health.GpuConversionFallbackError}' processGpuPriority={health.ProcessGpuPriority?.ToString() ?? "unavailable"}; " +
                $"encoder={Text(details, "encoder")} planned={Bool(details, "encoderPlanned")} zeroCopy={Text(details, "zeroCopyStatus")} slots={Number(details, "encoderSlots")} delay={Number(details, "encoderDelay")} maxInFlight={Number(details, "maxInFlight")} " +
                $"surfaces inUse={health.SurfacesInUse} peak={Number(details, "surfacesInUsePeak")} allocated={Number(details, "surfacesAllocated")} capacity={health.SurfaceCapacity} poolBytes={Number(details, "poolBytes")}; " +
                $"packets payload={Number(details, "packetPayloadBytes")} buffer={Number(details, "packetBufferBytes")} bytes; " +
                $"readback staging={Number(details, "readbackStagingInUse")}/{Number(details, "readbackStagingSlots")} peak={Number(details, "readbackStagingPeak")} cpuFrames={Number(details, "readbackCpuFramesInUse")}/{Number(details, "readbackCpuFrames")} peak={Number(details, "readbackCpuFramesPeak")} " +
                $"p50={Number(details, "readbackP50Ms"):F2} p95={Number(details, "readbackP95Ms"):F2}ms mapWait p50={Number(details, "readbackMapWaitP50Ms"):F2} p95={Number(details, "readbackMapWaitP95Ms"):F2}ms stalls={Number(details, "readbackMapStalls")} drops={Number(details, "readbackPressureDrops")} frameAllocations={Number(details, "frameAllocations")}; " +
                $"completion p50={Number(details, "completionP50Ms"):F1} p95={Number(details, "completionP95Ms"):F1} max={Number(details, "completionMaxMs"):F1}ms submission p50={Number(details, "submissionP50Ms"):F2} p95={Number(details, "submissionP95Ms"):F2} max={Number(details, "submissionMaxMs"):F2}ms; " +
                $"wgc callbacks={Number(details, "wgcCallbackFps"):F1} delivered={Number(details, "wgcDeliveredFps"):F1} overwritten={Number(details, "wgcOverwrittenFps"):F1}/s; " +
                $"acquired={Number(details, "acquiredFps"):F1} selection={Text(details, "frameSelection")} queue={Number(details, "sourceQueueDepth")}/{Number(details, "sourceQueueCapacity")} peak={Number(details, "sourceQueuePeak")} selectionDropped={Number(details, "selectionDroppedFps"):F1}/s duplicates={Number(details, "duplicateFps"):F1}/s replaced={Number(details, "pacingReplacedFps"):F1}/s; " +
                $"captureLatency p50={Number(details, "captureLatencyP50Ms"):F1} p95={Number(details, "captureLatencyP95Ms"):F1}ms; " +
                $"backpressure drops={Number(details, "backpressureDrops")} ({Number(details, "backpressureDropFps"):F1}/s) busy={Number(details, "encoderBusyDrops")} retained={Number(details, "retainedPressureDrops")} pool={Number(details, "poolPressureDrops")} waitMax={Number(details, "backpressureWaitMaxMs"):F1}ms stallRecoveries={Number(details, "encoderStallRecoveries")}.");
        }
        if (health.FullSession.State == FullSessionState.Recording && health.FullSession.OutputPath.Length > 0)
        {
            var publish = false;
            lock (_gate) publish = _publishedSessions.Add(health.FullSession.OutputPath);
            if (publish)
            {
                try { FullSessionPublication.Publish(configuration, health.FullSession.OutputPath); }
                catch (Exception publicationError) { AppLog.Error("Full Session metadata publication failed.", publicationError); }
            }
        }
        if (details.TryGetProperty("closedSessions", out var closed))
            foreach (var session in closed.EnumerateArray())
            {
                var sequence = session.GetProperty("sequence").GetInt64();
                var closedPath = Text(session, "path");
                lock (_gate) { if (!_closedSessions.Add(sequence)) continue; }
                if (closedPath.Length > 0 && Text(session, "error").Length == 0)
                {
                    try { FullSessionPublication.Complete(configuration, closedPath, (long)Number(session, "durationUs")); }
                    catch (Exception publicationError) { AppLog.Error("Full Session metadata completion failed.", publicationError); }
                    FullSessionClosed?.Invoke(this, closedPath);
                }
            }
    }
    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default,
        string? titleOverride = null, ReplayClipWindow? clipWindow = null, string? gameDisplayNameOverride = null, Guid? saveId = null)
    {
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0) throw new InvalidOperationException("A replay save is already in progress.");
        NativeRecorderSession? session = null; var retained = false; var accepted = false; var terminal = false;
        var id = saveId ?? Guid.NewGuid();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayBufferConfig configuration;
            lock (_gate)
            {
                if (!_recording || _session is null || _active is null) throw new InvalidOperationException("Replay buffer is not recording.");
                session = _session; configuration = _active; session.DangerousAddRef(ref retained);
            }
            var end = clipWindow?.EndUtc ?? MonotonicClock.UtcNow;
            var start = clipWindow?.StartUtc ?? end - TimeSpan.FromSeconds(configuration.DurationSeconds);
            var game = string.IsNullOrWhiteSpace(gameDisplayNameOverride) ? configuration.GameDisplayName : gameDisplayNameOverride;
            var title = string.IsNullOrWhiteSpace(titleOverride) ? game : titleOverride;
            var folder = Path.Combine(outputFolder, ClipFileNaming.BuildBaseName(game)); Directory.CreateDirectory(folder);
            var output = ClipFileNaming.BuildUniquePath(folder, ClipFileNaming.BuildFileName(title, DateTime.Now, "mp4", configuration.ClipFileNameScheme, configuration.CustomClipFileNameTemplate, game));
            session.BeginSave(id, ToUs(start), ToUs(end), output); accepted = true;
            var cancelled = false;
            while (true)
            {
                if (cancellationToken.IsCancellationRequested && !cancelled) { session.CancelSave(id); cancelled = true; }
                var result = session.ReadSave(id);
                if (result.Value.State != 0)
                {
                    terminal = true;
                    if (result.Value.State == 3) throw new OperationCanceledException(cancellationToken);
                    if (result.Value.State != 1) throw new IOException(Text(result.Details, "error"));
                    _lastFrozen = result.Value.Frozen != 0;
                    NativeRecordingPublication.Publish(configuration, output, game, title, result.Details, result.Value.DurationUs, ToUs(start));
                    return output;
                }
                await Task.Delay(40, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            try { if (accepted && terminal) session!.ReleaseSave(id); }
            finally
            {
                if (retained) session!.DangerousRelease();
                Interlocked.Exchange(ref _saving, 0);
            }
        }
    }
    private static string FullSessionPath(ReplayBufferConfig configuration)
    {
        if (!configuration.FullSessionRecordingEnabled || string.IsNullOrWhiteSpace(configuration.FullSessionRecordingFolder)) return "";
        Directory.CreateDirectory(configuration.FullSessionRecordingFolder);
        var title = string.IsNullOrWhiteSpace(configuration.GameDisplayName) ? "Session" : "Session - " + configuration.GameDisplayName;
        return ClipFileNaming.BuildUniquePath(configuration.FullSessionRecordingFolder,
            ClipFileNaming.BuildFileName(title, DateTime.Now, FullSessionFormat.Extension(configuration.FullSessionContainer),
                configuration.ClipFileNameScheme, configuration.CustomClipFileNameTemplate, configuration.GameDisplayName));
    }
    internal static long NowUs() => checked((long)(MonotonicClock.SharedSeconds * 1_000_000));
    internal static long ToUs(DateTime value) => checked((long)(MonotonicClock.ToSharedSeconds(value) * 1_000_000));
    internal static DateTime FromUs(long value) => MonotonicClock.UtcAnchor + TimeSpan.FromSeconds(value / 1_000_000d - (double)MonotonicClock.QpcAnchor / Stopwatch.Frequency);
    private static string Text(JsonElement value, string name) => !value.TryGetProperty(name, out var field) ? "" :
        field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : field.ValueKind == JsonValueKind.Number ? field.GetRawText() : "";
    private static bool Bool(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
    private static double Number(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.TryGetDouble(out var number) ? number : 0;
    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
