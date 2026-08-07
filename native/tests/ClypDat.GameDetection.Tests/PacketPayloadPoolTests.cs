using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class PacketPayloadPoolTests
{
    [Theory]
    [InlineData(1, PacketPayloadPool.QuantumBytes)]
    [InlineData(4096, 4096)]
    [InlineData(4097, 8192)]
    [InlineData(PacketPayloadPool.MaximumPooledBufferBytes, PacketPayloadPool.MaximumPooledBufferBytes)]
    [InlineData(PacketPayloadPool.MaximumPooledBufferBytes + 1, 0)]
    public void BucketLength_UsesFourKilobyteBucketsAndRejectsOversize(int requested, int expected)
    {
        Assert.Equal(expected, PacketPayloadPool.BucketLength(requested));
    }

    [Fact]
    public void RentAndReturn_ReusesMatchingPacketBuffer()
    {
        var pool = new PacketPayloadPool();
        var first = pool.Rent(70_000);
        pool.Return(first);

        var second = pool.Rent(70_000);

        Assert.Same(first, second);
        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public void Return_NeverRetainsMoreThanConfiguredLimit()
    {
        var pool = new PacketPayloadPool();
        var buffers = Enumerable.Range(0, 12)
            .Select(_ => pool.Rent(PacketPayloadPool.MaximumPooledBufferBytes))
            .ToArray();

        foreach (var buffer in buffers) pool.Return(buffer);

        Assert.Equal(PacketPayloadPool.MaximumRetainedBytes, pool.RetainedBytes);
    }

    [Fact]
    public void Deactivate_DropsRetainedBuffersAndRejectsLateReturns()
    {
        var pool = new PacketPayloadPool();
        var buffer = pool.Rent(50_000);
        pool.Return(buffer);

        pool.Deactivate();
        pool.Return(buffer);

        Assert.Equal(0, pool.RetainedBytes);
    }
}
