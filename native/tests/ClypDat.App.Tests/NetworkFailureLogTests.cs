using System.Net;
using System.Net.Http;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NetworkFailureLogTests
{
    [Fact]
    public void CountsAnOutageUntilTheSourceIsReachableAgain()
    {
        var source = $"test-{Guid.NewGuid():N}";
        var dns = new HttpRequestException("No such host is known.");

        Assert.True(NetworkFailureLog.Failed(source, dns));
        Assert.True(NetworkFailureLog.Failed(source, new TaskCanceledException("timeout", new TimeoutException())));
        Assert.Equal(2, NetworkFailureLog.Failures(source));

        NetworkFailureLog.Succeeded(source);
        Assert.Equal(0, NetworkFailureLog.Failures(source));
    }

    // An answer, even a bad one, means the network works: the caller logs it.
    [Fact]
    public void LeavesAnsweredAndUnrelatedFailuresToTheCaller()
    {
        var source = $"test-{Guid.NewGuid():N}";

        Assert.False(NetworkFailureLog.Failed(source, new HttpRequestException("Server error", null, HttpStatusCode.InternalServerError)));
        Assert.False(NetworkFailureLog.Failed(source, new InvalidDataException("bad feed")));
        Assert.Equal(0, NetworkFailureLog.Failures(source));
    }
}
