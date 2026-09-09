namespace ClypDat.App.Services;

// Route discovery can block outside this gate. Only its current generation may
// commit captures or timers; Stop invalidates all discoveries already in flight.
internal sealed class AudioRouteLifetime
{
    private readonly object _gate = new();
    private long _generation;
    private bool _active;
    public long Generation { get { lock (_gate) return _generation; } }
    public long Start(Action start)
    {
        lock (_gate) { _active = true; ++_generation; start(); return _generation; }
    }
    public void Stop(Action stop)
    {
        lock (_gate) { _active = false; ++_generation; stop(); }
    }
    public bool TryApply(long generation, Action apply)
    {
        lock (_gate)
        {
            if (!_active || generation != _generation) return false;
            apply();
            return true;
        }
    }
}
