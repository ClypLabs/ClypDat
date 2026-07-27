using System.Collections.Concurrent;

namespace ClypDat.App.Services;

internal sealed class ObsBridgeWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public ObsBridgeWorker()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ClypDat OBS"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public Task InvokeAsync(Action<ObsNativeBridge> action) => InvokeAsync(bridge =>
    {
        action(bridge);
        return true;
    });

    public Task<T> InvokeAsync<T>(Func<ObsNativeBridge, T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                completion.SetResult(action(_bridge!));
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        });
        return completion.Task;
    }

    private ObsNativeBridge? _bridge;

    private void Run()
    {
        _bridge = new ObsNativeBridge();
        foreach (var work in _queue.GetConsumingEnumerable()) work();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
    }
}
