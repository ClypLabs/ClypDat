using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class OscUdpListenerTests
{
    [Fact]
    public async Task DeliversIPv4AndIPv6OnDispatcherInOrderWithReceiptTime()
    {
        var dispatcher = new ConcurrentQueue<Action>();
        var packets = new List<OscReceivedPacket>();
        var marker = new object();
        using var listener = new OscUdpListener(dispatcher.Enqueue, packets.Add, _ => { }, () => marker);
        var port = FreePort();
        listener.Configure(true, port);
        Assert.StartsWith("Listening", listener.Status);
        var before = MonotonicClock.UtcNow;
        await Send(port, OscTestPackets.Bundle(OscTestPackets.Duration(30), OscTestPackets.Clip()));
        await Until(() => listener.PendingPackets == 1);
        var queued = MonotonicClock.UtcNow;
        Assert.Empty(packets);
        Drain(dispatcher);
        var packet = Assert.Single(packets);
        Assert.Same(marker, packet.ReceiptContext);
        Assert.InRange(packet.ReceivedUtc, before, queued);
        Assert.Equal(new[] { new OscCommand(OscCommandKind.ReplayDuration, 30), new(OscCommandKind.Clip) }, packet.Commands);
        if (listener.Status.Contains("IPv6"))
        {
            await Send(port, OscTestPackets.Clip(), IPAddress.IPv6Loopback);
            await Until(() => listener.PendingPackets == 1);
            Drain(dispatcher);
            Assert.Equal(2, packets.Count);
        }
    }

    [Fact]
    public void OccupiedPortReportsFailureWithoutSubstitution()
    {
        using var occupier = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        occupier.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)occupier.LocalEndPoint!).Port;
        using var listener = new OscUdpListener(_ => throw new Exception("Must not dispatch"), _ => { }, _ => { });
        listener.Configure(true, port);
        Assert.Contains($"Cannot listen on UDP {port}", listener.Status);
        Assert.Contains("port", listener.Status);
        Assert.True(listener.Completion.IsCompleted);
    }

    [Fact]
    public async Task DisableRebindAndShutdownInvalidateStaleCallbacksAndReleaseSockets()
    {
        var dispatcher = new ConcurrentQueue<Action>();
        var packets = new List<OscReceivedPacket>();
        using var listener = new OscUdpListener(dispatcher.Enqueue, packets.Add, _ => { });
        var first = FreePort();
        listener.Configure(true, first);
        await Send(first, OscTestPackets.Clip());
        await Until(() => listener.PendingPackets == 1);
        var completed = listener.Completion;
        listener.Configure(false, first);
        await completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("OSC disabled.", listener.Status);
        Assert.Equal(0, listener.PendingPackets);
        AssertPortReleased(first);

        listener.Configure(true, first);
        await Send(first, OscTestPackets.Duration(60));
        await Until(() => listener.PendingPackets == 1);
        var second = FreePort();
        completed = listener.Completion;
        listener.Configure(true, second);
        await completed.WaitAsync(TimeSpan.FromSeconds(5));
        AssertPortReleased(first);
        await Send(second, OscTestPackets.Duration(120));
        await Until(() => listener.PendingPackets == 1);
        Drain(dispatcher);
        Assert.Equal(120, Assert.Single(Assert.Single(packets).Commands).Seconds);
        Assert.False(packets[0].ListenerLifetime.IsCancellationRequested);

        await Send(second, OscTestPackets.Clip());
        await Until(() => listener.PendingPackets == 1);
        completed = listener.Completion;
        listener.Dispose();
        await completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(packets[0].ListenerLifetime.IsCancellationRequested);
        Drain(dispatcher);
        Assert.Single(packets);
        AssertPortReleased(second);
    }

    [Fact]
    public async Task MalformedAndOversizedTrafficDoesNotKillListenerOrPartlyDispatch()
    {
        var dispatcher = new ConcurrentQueue<Action>();
        var packets = new List<OscReceivedPacket>();
        using var listener = new OscUdpListener(dispatcher.Enqueue, packets.Add, _ => { });
        var port = FreePort();
        listener.Configure(true, port);
        await Send(port, new byte[6000]);
        await Send(port, OscTestPackets.Bundle(OscTestPackets.Duration(30), [1, 2, 3, 4]));
        await Send(port, OscTestPackets.Duration(120));
        await Until(() => listener.PendingPackets == 1);
        Drain(dispatcher);
        Assert.Equal(new OscCommand(OscCommandKind.ReplayDuration, 120), Assert.Single(Assert.Single(packets).Commands));
    }

    [Fact]
    public async Task PendingQueueIsBoundedAndOnlyOneDrainIsPosted()
    {
        var dispatcher = new ConcurrentQueue<Action>();
        var packets = new List<OscReceivedPacket>();
        var receptions = 0;
        using var listener = new OscUdpListener(dispatcher.Enqueue, packets.Add, _ => { }, () => Interlocked.Increment(ref receptions));
        var port = FreePort();
        listener.Configure(true, port);
        for (var i = 0; i < 40; i++) await Send(port, OscTestPackets.Clip());
        await Until(() => Volatile.Read(ref receptions) == 40);
        Assert.Equal(32, listener.PendingPackets);
        Assert.Single(dispatcher);
        Drain(dispatcher);
        Assert.Equal(32, packets.Count);
        Assert.Equal(0, listener.PendingPackets);
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static void AssertPortReleased(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    private static async Task Send(int port, byte[] bytes, IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        using var client = new UdpClient(address.AddressFamily);
        await client.SendAsync(bytes, new IPEndPoint(address, port));
    }

    private static void Drain(ConcurrentQueue<Action> dispatcher) { while (dispatcher.TryDequeue(out var action)) action(); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
