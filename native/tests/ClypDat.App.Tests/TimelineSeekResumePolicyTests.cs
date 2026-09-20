using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class TimelineSeekResumePolicyTests
{
    [Fact]
    public void ReplacementSeek_PreservesActivePlaybackIntent()
    {
        Assert.True(TimelineSeekResumePolicy.Resolve(
            requestedResume: false,
            inFlightSeekWantsResume: true));
    }
}
