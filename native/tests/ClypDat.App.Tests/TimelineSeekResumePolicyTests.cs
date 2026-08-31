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

    [Fact]
    public void FailedPlayingSeek_ContinuesPlayback()
    {
        Assert.True(TimelineSeekResumePolicy.ShouldContinueAfterSeekFailure(resumePlayback: true));
        Assert.False(TimelineSeekResumePolicy.ShouldContinueAfterSeekFailure(resumePlayback: false));
    }
}
