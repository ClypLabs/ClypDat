namespace ClypDat.App.Services;

// Session ownership survives failed joins: a live native caller must keep its
// cancellation source, queue, completion events and GPU resources alive.
internal sealed class ReplaySessionLifetime : IDisposable
{
    private readonly CancellationTokenSource _stopping;
    private readonly List<ManualResetEventSlim> _completions = new();
    private bool _workersStopped;
    private bool _disposed;
    internal Exception? ShutdownError { get; private set; }
    internal object NativeGate { get; } = new();
    internal CancellationToken Token => _stopping.Token;
    internal ReplaySessionLifetime(CancellationToken token) => _stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
    internal IDisposable EnterNative() => new NativeScope(NativeGate);
    internal ManualResetEventSlim CreateSwapCompletion()
    {
        var completion = new ManualResetEventSlim();
        _completions.Add(completion);
        return completion;
    }
    internal bool StopWorkers(Func<bool> stopProducer, Action closeQueue, Func<bool> stopEncoder)
    {
        try
        {
            _stopping.Cancel();
            if (!stopProducer()) return false;
            closeQueue();
            _workersStopped = stopEncoder();
            return _workersStopped;
        }
        catch (Exception error)
        {
            ShutdownError = error;
            return false;
        }
    }
    public void Dispose()
    {
        if (!_workersStopped || _disposed) return;
        _disposed = true;
        foreach (var completion in _completions) completion.Dispose();
        _stopping.Dispose();
    }
    private sealed class NativeScope : IDisposable
    {
        private object? _gate;
        internal NativeScope(object gate) { Monitor.Enter(gate); _gate = gate; }
        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is not null) Monitor.Exit(gate);
        }
    }
}

internal sealed class AcquiredCaptureFrame(Action release) : IDisposable
{
    internal bool Acquired { get; set; }
    public void Dispose()
    {
        if (!Acquired) return;
        Acquired = false;
        release();
    }
}
