using System.Threading.Channels;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

/// <summary>
/// What the sandboxed detector host drives, whichever game is being watched.
/// </summary>
internal interface ILiveGameDetector : IAsyncDisposable
{
    event EventHandler<AutoClipDetectorEvent>? Detected;
    event EventHandler<string>? StatusChanged;
    void ApplyPolicy(bool enabled, IEnumerable<string> enabledEventIds);
    void Offer(DetectorFrameSnapshot frame);
}

internal sealed class LiveOverwatchDetector : ILiveGameDetector
{
    private readonly Channel<DetectorFrameSnapshot> _frames = Channel.CreateBounded<DetectorFrameSnapshot>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly WindowsOcrFrameReader _ocr = new();
    private readonly OverwatchDetector _detector = new();
    // Loaded once: the banners cannot be read, only recognised.
    private IReadOnlyList<LoadedTemplate> _templates = Array.Empty<LoadedTemplate>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private HashSet<string> _enabledEvents = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _enabled;

    public LiveOverwatchDetector() => _worker = Task.Run(ProcessAsync);

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
        if (enabled && _templates.Count == 0)
            _templates = DetectorTemplates.Load("overwatch", DetectorRegions.ForGame("overwatch")!);
        StatusChanged?.Invoke(this, !enabled
            ? "Disabled"
            : _templates.Count == 0
                ? "Watching — streak banners off, templates missing"
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
                    var leftColumn = await _ocr.ReadTextAsync(frame.First).ConfigureAwait(false);
                    var killFeed = await _ocr.ReadTextAsync(frame.Second).ConfigureAwait(false);
                    var teamKill = await _ocr.ReadTextAsync(frame.Third).ConfigureAwait(false);
                    var timestamp = TimeSpan.FromTicks(frame.CapturedUtc.Ticks);
                    // Only the strongest hit per slot: the streak templates all
                    // share one band, and a quintuple correlates against the
                    // quadruple reference too.
                    var banners = DetectorTemplates.Match(_templates, frame)
                        .GroupBy(hit => hit.Template.Slot)
                        .Select(group => group.First())
                        .Select(hit => new DetectedBanner(hit.Template.EventId, hit.Template.Label))
                        .ToArray();
                    foreach (var item in _detector.Observe(new OverwatchFrameObservation(
                                 timestamp, leftColumn, killFeed, teamKill, banners)))
                    {
                        if (!_enabled || !_enabledEvents.Contains(item.EventId)) continue;
                        // Play of the Game needs a long lead: the banner only
                        // appears once the replay is already running.
                        var (lead, tail) = item.EventId switch
                        {
                            "play-of-the-game" => (15, 10),
                            "team-kill" => (10, 8),
                            "elimination" => (8, 6),
                            _ => (8, 6)
                        };
                        Detected?.Invoke(this, new AutoClipDetectorEvent(
                            "overwatch", item.EventId, item.Label, item.OccurrenceId,
                            item.Confidence, frame.CapturedUtc, lead, tail));
                    }
                }
                catch (Exception error)
                {
                    CaptureWorkerLog.Error("Overwatch detector frame failed.", error);
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
