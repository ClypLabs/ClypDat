using System.Net;
using System.Net.Sockets;

namespace ClypDat.App.Services;

internal sealed record OscReceivedPacket(IReadOnlyList<OscCommand> Commands, DateTime ReceivedUtc,
    CancellationToken ListenerLifetime, object? ReceiptContext)
{
    // Duration changes apply synchronously. Starting a save never waits here,
    // so the next clip sees the busy/restart state and cannot queue a save.
    public void Dispatch(Action<int> duration, Action<OscReceivedPacket> clip)
    {
        foreach (var command in Commands)
        {
            if (ListenerLifetime.IsCancellationRequested) return;
            if (command.Kind == OscCommandKind.ReplayDuration) duration(command.Seconds);
            else clip(this);
        }
    }
}

internal sealed class OscUdpListener : IDisposable
{
    internal const int MaximumPendingPackets = 32;
    private readonly object _gate = new();
    private readonly Queue<OscReceivedPacket> _pending = new();
    private readonly Action<Action> _dispatch;
    private readonly Action<OscReceivedPacket> _receive;
    private readonly Action<string> _statusChanged;
    private readonly Func<object?> _receiptContext;
    private Socket? _socket;
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private bool _drainScheduled, _disposed;
    private int _port;

    public OscUdpListener(Action<Action> dispatch, Action<OscReceivedPacket> receive,
        Action<string> statusChanged, Func<object?>? receiptContext = null)
    {
        _dispatch = dispatch;
        _receive = receive;
        _statusChanged = statusChanged;
        _receiptContext = receiptContext ?? (() => null);
    }

    public string Status { get; private set; } = "OSC disabled.";
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal int PendingPackets { get { lock (_gate) return _pending.Count; } }

    // Configure and Dispose run on the same dispatcher as command delivery.
    public void Configure(bool enabled, int port)
    {
        if (_disposed) return;
        if (enabled && _socket is not null && _port == port) return;
        Stop();
        if (!enabled) { SetStatus("OSC disabled."); return; }
        if (port is < 1 or > 65535) { SetStatus("OSC port must be between 1 and 65535."); return; }
        Socket? socket = null;
        try
        {
            socket = Bind(port);
            _socket = socket;
            _port = port;
            var cancellation = _cancellation = new CancellationTokenSource();
            var generation = _generation;
            SetStatus($"Listening on UDP {port} ({(socket.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv4 and IPv6" : "IPv4")}).");
            Completion = Task.Run(() => ReceiveAsync(socket, generation, cancellation));
        }
        catch (SocketException error)
        {
            socket?.Dispose();
            SetStatus(error.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Cannot listen on UDP {port}: port is already in use. Choose another port or close the other listener."
                : $"Cannot listen on UDP {port}: {error.Message} Choose another port or close conflicting listeners; check network permissions, then disable and enable OSC to retry.");
            AppLog.Info($"OSC listener failed: {Status}");
        }
    }

    private static Socket Bind(int port)
    {
        if (Socket.OSSupportsIPv6)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
                    { DualMode = true, ExclusiveAddressUse = true };
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return socket;
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressFamilyNotSupported
                or SocketError.ProtocolNotSupported or SocketError.OperationNotSupported or SocketError.AddressNotAvailable)
            {
                socket?.Dispose();
            }
            catch (NotSupportedException) { socket?.Dispose(); }
            catch { socket?.Dispose(); throw; }
        }
        var ipv4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        try { ipv4.Bind(new IPEndPoint(IPAddress.Any, port)); return ipv4; }
        catch { ipv4.Dispose(); throw; }
    }

    private async Task ReceiveAsync(Socket socket, long generation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var bytes = new byte[OscPacketDecoder.MaximumPacketBytes + 1];
        EndPoint remote = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!token.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try { received = await socket.ReceiveFromAsync(bytes.AsMemory(), SocketFlags.None, remote, token).ConfigureAwait(false); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize) { continue; }
                var receivedUtc = MonotonicClock.UtcNow;
                var context = _receiptContext();
                if (!OscPacketDecoder.TryDecode(bytes.AsSpan(0, received.ReceivedBytes), out var commands))
                {
                    AppLog.Debug("OSC packet rejected: malformed, unsupported, scheduled or over limit.");
                    continue;
                }
                if (commands.Count == 0) continue;
                var schedule = false;
                lock (_gate)
                {
                    if (generation != _generation || token.IsCancellationRequested) return;
                    if (_pending.Count >= MaximumPendingPackets)
                    {
                        AppLog.Debug("OSC packet dropped: pending packet limit reached.");
                        continue;
                    }
                    _pending.Enqueue(new(commands, receivedUtc, token, context));
                    if (!_drainScheduled) { _drainScheduled = true; schedule = true; }
                }
                if (schedule) _dispatch(() => Drain(generation));
            }
        }
        catch (Exception error) when (token.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception error)
        {
            _dispatch(() =>
            {
                if (generation != _generation) return;
                Stop();
                SetStatus($"OSC listener stopped: {error.Message} Disable and enable OSC to retry.");
                AppLog.Error("OSC listener stopped", error);
            });
        }
        finally { cancellation.Dispose(); }
    }

    private void Drain(long generation)
    {
        for (var count = 0; count < MaximumPendingPackets; count++)
        {
            OscReceivedPacket packet;
            lock (_gate)
            {
                if (generation != _generation) return;
                if (!_pending.TryDequeue(out packet!)) { _drainScheduled = false; return; }
            }
            if (packet.ListenerLifetime.IsCancellationRequested) continue;
            try { _receive(packet); }
            catch (Exception error) { AppLog.Error("OSC command dispatch failed", error); }
        }
        lock (_gate)
        {
            if (generation != _generation) return;
            if (_pending.Count == 0) { _drainScheduled = false; return; }
        }
        // Yield to other UI work even if traffic arrives faster than dispatch.
        _dispatch(() => Drain(generation));
    }

    private void SetStatus(string status)
    {
        if (Status != status) AppLog.Info($"[OSC] {status}");
        Status = status;
        _statusChanged(status);
    }

    private void Stop()
    {
        lock (_gate)
        {
            _generation++;
            _pending.Clear();
            _drainScheduled = false;
            // The receive loop owns disposal of its cancellation source.
            if (_cancellation is { } cancellation)
            {
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            }
            _cancellation = null;
            _socket?.Dispose();
            _socket = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
