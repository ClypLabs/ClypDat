using System.Threading.Channels;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed class LiveHelldivers2Detector : ILiveGameDetector
{
    private readonly Channel<(DetectorFrameSnapshot Frame, long Generation)> _frames = Channel.CreateBounded<(DetectorFrameSnapshot, long)>(
        new BoundedChannelOptions(1)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly WindowsOcrFrameReader _ocr = new();
    private readonly Helldivers2CounterReader _counterReader;
    private readonly Helldivers2Detector _detector = new();
    private readonly object _gate = new();
    private long _generation;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private HashSet<string> _enabledEvents = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _enabled;

    public LiveHelldivers2Detector()
    {
        _counterReader = new Helldivers2CounterReader(_ocr.ReadTextAsync);
        _worker = Task.Run(ProcessAsync);
    }

    public event EventHandler<AutoClipDetectorEvent>? Detected;
    public event EventHandler<string>? StatusChanged;

    public void ApplyPolicy(bool enabled, IEnumerable<string> enabledEventIds)
    {
        lock (_gate)
        {
            _enabledEvents = new HashSet<string>(enabledEventIds, StringComparer.OrdinalIgnoreCase);
            _enabled = enabled;
            if (!enabled)
            {
                _generation++;
                while (_frames.Reader.TryRead(out _)) { }
                _detector.ResetSession();
            }
        }
        StatusChanged?.Invoke(this, enabled ? "Watching" : "Disabled");
    }

    public void Offer(DetectorFrameSnapshot frame)
    {
        lock (_gate)
            if (_enabled) _frames.Writer.TryWrite((frame, _generation));
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var (frame, generation) in _frames.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    var center = await _ocr.ReadTextAsync(frame.First).ConfigureAwait(false);
                    var mission = await _ocr.ReadTextAsync(frame.Second).ConfigureAwait(false);
                    var counter = await _counterReader.ReadAsync(frame.Third).ConfigureAwait(false);
                    var timestamp = TimeSpan.FromTicks(frame.CapturedUtc.Ticks);
                    lock (_gate)
                    {
                        if (!_enabled || generation != _generation) continue;
                        foreach (var item in _detector.Observe(new Helldivers2FrameObservation(
                                     timestamp, center, mission, counter.Text, counter.Visibility, counter.Count), _enabledEvents))
                        {
                            if (!_enabled || !_enabledEvents.Contains(item.EventId)) continue;
                            Detected?.Invoke(this, ToAutoClipEvent(item));
                        }
                    }
                }
                catch (Exception error)
                {
                    lock (_gate)
                        if (generation == _generation) _detector.ObserveCaptureFailure();
                    CaptureWorkerLog.Error("Helldivers detector frame failed.", error);
                    StatusChanged?.Invoke(this, "Degraded — OCR frame failed");
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    internal static AutoClipDetectorEvent ToAutoClipEvent(Helldivers2DetectedEvent item)
    {
        var (lead, tail) = item.EventId switch
        {
            "eliminated" => (12, 6),
            "successful-mission" => (15, 10),
            _ => (10, 6)
        };
        return new AutoClipDetectorEvent("helldivers2", item.EventId, item.Label, item.OccurrenceId,
            item.Confidence, new DateTime(item.Timestamp.Ticks, DateTimeKind.Utc), lead, tail,
            item.StreakStart is { } start ? new DateTime(start.Ticks, DateTimeKind.Utc) : null);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _frames.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
