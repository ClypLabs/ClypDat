using System.Threading.Channels;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed class LiveFortniteDetector : ILiveGameDetector
{
    private readonly Channel<DetectorFrameSnapshot> _frames = Channel.CreateBounded<DetectorFrameSnapshot>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly WindowsOcrFrameReader _ocr = new();
    private readonly FortniteDetector _detector = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private HashSet<string> _enabledEvents = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _enabled;

    public LiveFortniteDetector() => _worker = Task.Run(ProcessAsync);

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
            StatusChanged?.Invoke(this, "Disabled");
            return;
        }

        // Read here rather than per frame: it is one file read, and the answer
        // cannot change without a new login.
        var localPlayer = FortniteIdentity.Resolve();
        _detector.SetLocalPlayer(localPlayer);
        // Without a name the kill feed cannot be attributed, so those events
        // stay silent instead of clipping other players' kills. The banner and
        // upper-centre events are about the local player by construction and
        // keep working.
        StatusChanged?.Invoke(this, localPlayer is null
            ? "Watching — feed events off, Fortnite display name not found"
            : "Watching");
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
                    var killFeed = await _ocr.ReadTextAsync(frame.First).ConfigureAwait(false);
                    var banner = await _ocr.ReadTextAsync(frame.Second).ConfigureAwait(false);
                    var upperCentre = await _ocr.ReadTextAsync(frame.Third).ConfigureAwait(false);
                    var timestamp = TimeSpan.FromTicks(frame.CapturedUtc.Ticks);
                    foreach (var item in _detector.Observe(new FortniteFrameObservation(
                                 timestamp, killFeed, banner, upperCentre)))
                    {
                        if (!_enabled || !_enabledEvents.Contains(item.EventId)) continue;
                        var (lead, tail) = item.EventId switch
                        {
                            "victory-royale" => (15, 10),
                            "got-eliminated" => (12, 6),
                            "enemy-team-wiped" => (8, 8),
                            "double-elimination" or "multi-elimination" => (6, 8),
                            _ => (8, 6)
                        };
                        Detected?.Invoke(this, new AutoClipDetectorEvent(
                            "fortnite", item.EventId, item.Label, item.OccurrenceId,
                            item.Confidence, frame.CapturedUtc, lead, tail));
                    }
                }
                catch (Exception error)
                {
                    CaptureWorkerLog.Error("Fortnite detector frame failed.", error);
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
