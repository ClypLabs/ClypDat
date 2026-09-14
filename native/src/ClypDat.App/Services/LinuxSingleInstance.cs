using System.IO.Pipes;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed class LinuxSingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task? _listener;
    private static string Name => "ClypDat-Activation-" + CaptureWorkerProtocol.UserSuffix();
    public bool IsOwner { get; }

    public LinuxSingleInstance(Action activate)
    {
        _mutex = new Mutex(true, Name, out var owner);
        IsOwner = owner;
        if (owner)
            _listener = ListenAsync(activate);
        else
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            client.WriteByte(1);
        }
    }

    private async Task ListenAsync(Action activate)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                var data = new byte[1];
                if (await server.ReadAsync(data, _stop.Token) == 1 && data[0] == 1) activate();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { AppLog.Error("Linux activation listener failed.", error); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.GetAwaiter().GetResult();
        if (IsOwner) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _stop.Dispose();
    }
}
