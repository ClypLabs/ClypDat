namespace ClypDat.App.Services;

// Intent changes immediately; resource transitions serialize before waiting for saves.
internal sealed class CaptureLifecycleCoordinator(
    SemaphoreSlim transitions, SemaphoreSlim saves, Func<bool> isRecording,
    Func<CancellationToken, Task> start, Func<CancellationToken, Task> stop,
    Action<bool, bool> completed)
{
    private readonly object _state = new();
    private bool _requested;
    private bool _available;
    private long _stopRevision;
    private long _completedStopRevision;
    public bool Requested { get { lock (_state) return _requested; } }
    public bool Available { get { lock (_state) return _available; } }

    public void Request(bool requested)
    {
        lock (_state)
        {
            _requested = requested;
            if (!requested) _stopRevision++;
        }
    }

    public void SetAvailability(bool available)
    {
        lock (_state)
        {
            if (_available && !available) _stopRevision++;
            _available = available;
        }
    }

    public async Task ReconcileAsync(CancellationToken token)
    {
        await transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                bool requested, available;
                long stopRevision;
                lock (_state) { requested = _requested; available = _available; stopRevision = _stopRevision; }
                if (stopRevision != _completedStopRevision || ((!requested || !available) && isRecording()))
                {
                    await saves.WaitAsync(token).ConfigureAwait(false);
                    try { if (isRecording()) await stop(token).ConfigureAwait(false); }
                    finally { saves.Release(); }
                    _completedStopRevision = stopRevision;
                    completed(false, !Available);
                    // Wake or disable may have arrived during either wait.
                    continue;
                }
                if (!requested || !available || isRecording()) return;
                await start(token).ConfigureAwait(false);
                completed(true, false);
                // Suspend during StartAsync must complete teardown before any restart.
                lock (_state)
                    if (_requested && _available && _stopRevision == _completedStopRevision) return;
            }
        }
        finally { transitions.Release(); }
    }
}
