using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FullSessionFinalizeTests
{
    // The mux used to write "<session>.mp4.muxing.mp4" straight into the VODs
    // folder, where the library watcher turned it into a second card for the
    // whole encode - one playing silent video, one probing as audio-only.
    [Fact]
    public void MuxStagingFolderIsInvisibleToTheLibrary()
    {
        const string staged = @"D:\Videos\ClypDat\VODs\Fortnite\.clypdat-mux-abc123\Session - Fortnite.mp4";
        const string session = @"D:\Videos\ClypDat\VODs\Fortnite\Session - Fortnite.mp4";

        Assert.False(MediaProbeService.IsVideoFile(staged));
        Assert.True(MediaProbeService.IsVideoFile(session));
    }

    // The old sibling name was a plain .mp4 as far as the scan was concerned,
    // which is exactly how it became a card.
    [Fact]
    public void TheOldSiblingMuxNameWasIndistinguishableFromAClip()
    {
        Assert.True(MediaProbeService.IsVideoFile(@"D:\Videos\ClypDat\VODs\Fortnite\Session - Fortnite.mp4.muxing.mp4"));
    }

}
