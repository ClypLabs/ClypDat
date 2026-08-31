namespace ClypDat.App.Services;

internal sealed class EditorSeekRequestQueue
{
    internal static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan FinalQuietPeriod = TimeSpan.FromMilliseconds(100);
    private readonly object _sync = new();
    private TimeSpan? _preview;
    private bool _finalSeekPending;
    private long _finalSeekGeneration;
    private long _generation;
    private DateTimeOffset? _lastPreviewWrite;
    private int _previewWritesSinceFinal;
    private int _previewRequestsSinceFinal;
    private int _suppressedStaleParksSinceFinal;

    public long QueuePreview(TimeSpan target)
    {
        lock (_sync)
        {
            if (_finalSeekPending) return _generation;
            _generation++;
            _preview = Normalize(target);
            _previewRequestsSinceFinal++;
            return _generation;
        }
    }

    public bool TryQueuePreview(TimeSpan target)
    {
        lock (_sync)
        {
            if (_finalSeekPending) return false;
            _generation++;
            _preview = Normalize(target);
            _previewRequestsSinceFinal++;
            return true;
        }
    }

    public EditorFinalSeekRequest BeginFinalSeek(DateTimeOffset now)
    {
        lock (_sync)
        {
            _preview = null;
            _finalSeekPending = true;
            _suppressedStaleParksSinceFinal = 0;
            _generation++;
            _finalSeekGeneration = _generation;
            var quietUntil = _lastPreviewWrite is { } lastWrite
                ? lastWrite + FinalQuietPeriod
                : now;
            var request = new EditorFinalSeekRequest(
                _finalSeekGeneration,
                _previewRequestsSinceFinal,
                _previewWritesSinceFinal,
                quietUntil > now ? quietUntil - now : TimeSpan.Zero);
            _previewRequestsSinceFinal = 0;
            _previewWritesSinceFinal = 0;
            _lastPreviewWrite = null;
            return request;
        }
    }

    public long BeginFinalSeek() => BeginFinalSeek(DateTimeOffset.UtcNow).Generation;

    public void CompleteFinalSeek(long generation = 0)
    {
        lock (_sync)
        {
            if (generation == 0 || generation == _finalSeekGeneration) _finalSeekPending = false;
        }
    }

    public bool TryTakePreview(DateTimeOffset now, out TimeSpan target, out long generation, out TimeSpan delay)
    {
        lock (_sync)
        {
            if (_finalSeekPending || _preview is null)
            {
                target = default;
                generation = 0;
                delay = TimeSpan.Zero;
                return false;
            }

            if (_lastPreviewWrite is { } lastWrite && now - lastWrite < PreviewInterval)
            {
                target = default;
                generation = 0;
                delay = PreviewInterval - (now - lastWrite);
                return false;
            }

            target = _preview.Value;
            _preview = null;
            generation = _generation;
            delay = TimeSpan.Zero;
            return true;
        }
    }

    public bool TryTakePreview(out TimeSpan target, out long generation) =>
        TryTakePreview(DateTimeOffset.UtcNow, out target, out generation, out _);

    public bool HasPendingPreview()
    {
        lock (_sync) return !_finalSeekPending && _preview is not null;
    }

    public void MarkPreviewWritten(long generation, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_finalSeekPending || generation != _generation) return;
            _lastPreviewWrite = now;
            _previewWritesSinceFinal++;
        }
    }

    // Holds the queue lock through one LibVLC preview mutation. BeginFinalSeek
    // cannot invalidate a generation between this check and that mutation.
    public PreviewTransportLease? TryAcquirePreviewTransport(long generation, bool parking = false)
    {
        Monitor.Enter(_sync);
        if (!_finalSeekPending && generation != 0 && generation == _generation)
        {
            return new PreviewTransportLease(this);
        }

        if (parking) _suppressedStaleParksSinceFinal++;
        Monitor.Exit(_sync);
        return null;
    }

    public EditorPreviewHandoffSummary GetHandoffSummary()
    {
        lock (_sync)
        {
            return new EditorPreviewHandoffSummary(
                _finalSeekPending ? "final-owner" : "complete",
                _suppressedStaleParksSinceFinal);
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_sync) return !_finalSeekPending && generation == _generation;
    }

    internal static TimeSpan Normalize(TimeSpan target) =>
        TimeSpan.FromMilliseconds(Math.Max(0, (long)target.TotalMilliseconds));

    internal static bool ShouldResume(bool previousPlaying, bool seekSucceeded) =>
        previousPlaying && seekSucceeded;

    internal sealed class PreviewTransportLease : IDisposable
    {
        private EditorSeekRequestQueue? _owner;

        internal PreviewTransportLease(EditorSeekRequestQueue owner) => _owner = owner;

        public void MarkWritten(DateTimeOffset now)
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(PreviewTransportLease));
            owner._lastPreviewWrite = now;
            owner._previewWritesSinceFinal++;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null) Monitor.Exit(owner._sync);
        }
    }
}

internal readonly record struct EditorFinalSeekRequest(
    long Generation,
    int PreviewRequestCount,
    int PreviewWriteCount,
    TimeSpan QuietPeriod);

internal readonly record struct EditorPreviewHandoffSummary(
    string Outcome,
    int SuppressedStaleParks);
