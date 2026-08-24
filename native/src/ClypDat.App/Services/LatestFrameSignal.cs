namespace ClypDat.App.Services;

// One-slot event signal for callback-driven capture. Producers never wait on
// consumers; repeated publications coalesce into one wake-up and the consumer
// takes the newest publication version.
internal sealed class LatestFrameSignal : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _ready = new(0, 1);
    private long _published;
    private long _taken;
    private long _overwritten;
    private bool _disposed;

    public void Publish()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_published > _taken) _overwritten++;
            _published++;
            if (_ready.CurrentCount == 0) _ready.Release();
        }
    }

    public void RecordOverwritten(long count)
    {
        if (count <= 0) return;
        lock (_gate)
            if (!_disposed) _overwritten += count;
    }

    public bool WaitAndTake(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            if (!_ready.Wait(timeout, cancellationToken)) return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed || _published == _taken) return false;
            _taken = _published;
            return true;
        }
    }

    public void Wake()
    {
        lock (_gate)
            if (!_disposed && _ready.CurrentCount == 0) _ready.Release();
    }

    public LatestFrameSignalSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new LatestFrameSignalSnapshot(_published, _taken, _overwritten);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _ready.Dispose();
    }
}

internal readonly record struct LatestFrameSignalSnapshot(long Published, long Taken, long Overwritten);
