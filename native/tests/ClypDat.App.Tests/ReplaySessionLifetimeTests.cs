using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ReplaySessionLifetimeTests
{
    [Fact]
    public async Task NativeCaptureAndEncoderCallsCannotOverlap()
    {
        using var session = new ReplaySessionLifetime(CancellationToken.None);
        using var attempted = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        Task encoder;
        using (session.EnterNative())
        {
            encoder = Task.Run(() => { attempted.Set(); using (session.EnterNative()) entered.Set(); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(entered.Wait(TimeSpan.FromMilliseconds(100)));
        }
        await encoder.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(entered.IsSet);
        Assert.True(session.StopWorkers(() => true, () => { }, () => true));
    }

    [Fact]
    public void FailedJoinPreservesResourcesAndLateCompletion()
    {
        var session = new ReplaySessionLifetime(CancellationToken.None);
        var completion = session.CreateSwapCompletion();
        Assert.False(session.StopWorkers(() => throw new InvalidOperationException("join failed"),
            () => throw new Exception("queue closed too early"), () => true));
        Assert.IsType<InvalidOperationException>(session.ShutdownError);
        session.Dispose();
        completion.Set();
        Assert.True(session.StopWorkers(() => true, () => { }, () => true));
        session.Dispose();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ShutdownStopsProducerBeforeClosingQueueAndRetainsLateAcknowledgements(bool producerStops, bool encoderStops)
    {
        var session = new ReplaySessionLifetime(CancellationToken.None);
        var completion = session.CreateSwapCompletion();
        var calls = new List<string>();
        var stopped = session.StopWorkers(
            () => { Assert.True(session.Token.IsCancellationRequested); calls.Add("producer"); return producerStops; },
            () => calls.Add("close"),
            () => { calls.Add("encoder"); return encoderStops; });
        Assert.Equal(producerStops && encoderStops, stopped);
        Assert.Equal(producerStops ? new[] { "producer", "close", "encoder" } : new[] { "producer" }, calls);
        if (!stopped)
        {
            session.Dispose(); // unsafe shutdown must preserve a late encoder acknowledgement
            completion.Set();
            Assert.True(completion.IsSet);
            Assert.True(session.StopWorkers(() => true, () => { }, () => true));
        }
        session.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcquisitionReleasesExactlyOnceEvenWhenProcessingThrows(bool acquired)
    {
        var releases = 0;
        var lease = new AcquiredCaptureFrame(() => releases++);
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (lease)
            {
                lease.Acquired = acquired;
                throw new InvalidOperationException("processing failed");
            }
        }));
        lease.Dispose();
        Assert.Equal(acquired ? 1 : 0, releases);
    }
}
