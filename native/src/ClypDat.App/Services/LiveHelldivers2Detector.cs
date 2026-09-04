using System.Threading.Channels;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed class LiveHelldivers2Detector : IAsyncDisposable
{
    private readonly Channel<DetectorFrameSnapshot> _frames = Channel.CreateBounded<DetectorFrameSnapshot>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly WindowsOcrFrameReader _ocr = new();
    private readonly Helldivers2Detector _detector = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private HashSet<string> _enabledEvents = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _enabled;

    public LiveHelldivers2Detector() => _worker = Task.Run(ProcessAsync);

    public event EventHandler<AutoClipDetectorEvent>? Detected;
    public event EventHandler<string>? StatusChanged;

    public void ApplyPolicy(bool enabled, IEnumerable<string> enabledEventIds)
    {
        _enabledEvents = new HashSet<string>(enabledEventIds, StringComparer.OrdinalIgnoreCase);
        _enabled = enabled;
        if (!enabled)
        {
            while (_frames.Reader.TryRead(out _)) { }
            _detector.ResetSession();
        }
        StatusChanged?.Invoke(this, enabled ? "Watching" : "Disabled");
    }

    public void Offer(DetectorFrameSnapshot frame)
    {
        if (_enabled) _frames.Writer.TryWrite(frame);
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    var center = await _ocr.ReadTextAsync(frame.CenterBanner).ConfigureAwait(false);
                    var mission = await _ocr.ReadTextAsync(frame.MissionPanel).ConfigureAwait(false);
                    var counter = await _ocr.ReadTextAsync(frame.KillCounter).ConfigureAwait(false);
                    var timestamp = TimeSpan.FromTicks(frame.CapturedUtc.Ticks);
                    foreach (var item in _detector.Observe(new Helldivers2FrameObservation(
                                 timestamp, center, mission, counter)))
                    {
                        if (!_enabled || !_enabledEvents.Contains(item.EventId)) continue;
                        var (lead, tail) = item.EventId switch
                        {
                            "eliminated" => (12, 6),
                            "successful-mission" => (15, 10),
                            _ => (10, 6)
                        };
                        Detected?.Invoke(this, new AutoClipDetectorEvent(
                            "helldivers2", item.EventId, item.Label, item.OccurrenceId,
                            item.Confidence, frame.CapturedUtc, lead, tail));
                    }
                }
                catch (Exception error)
                {
                    CaptureWorkerLog.Error("Helldivers detector frame failed.", error);
                    StatusChanged?.Invoke(this, "Degraded — OCR frame failed");
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _frames.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
