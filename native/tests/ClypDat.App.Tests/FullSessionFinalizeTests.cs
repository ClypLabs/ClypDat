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

    [Fact]
    public void ProgressBelowFivePercentDoesNotInventACountdown()
    {
        var entry = new FullSessionFinalizeProgress(
            @"D:\clip.mp4", SessionSeconds: 600, MuxedSeconds: 1, Reencoding: false, StartedUtc: DateTime.UtcNow.AddSeconds(-3));

        var text = MainWindowViewModel.DescribeFinalize(entry);

        Assert.Contains("Adding session audio", text, StringComparison.Ordinal);
        Assert.DoesNotContain("left", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressReportsPercentAndRemainingOnceUnderway()
    {
        // Half the session muxed in 60s, so the whole encode is ~120s and ~60s
        // of it remain - before the faststart tail is accounted for.
        var entry = new FullSessionFinalizeProgress(
            @"D:\clip.mp4", SessionSeconds: 600, MuxedSeconds: 300, Reencoding: false, StartedUtc: DateTime.UtcNow.AddSeconds(-60));

        var text = MainWindowViewModel.DescribeFinalize(entry);

        Assert.Contains("Adding session audio", text, StringComparison.Ordinal);
        Assert.Contains("left", text, StringComparison.Ordinal);
        // Scaled by the 0.85 encode share, half a session reads as 42%, never
        // as 50% - the +faststart rewrite is real work still to come.
        Assert.Contains("42%", text, StringComparison.Ordinal);
    }

    // Parking at "99% - 1s left" for the length of a whole file rewrite reads
    // as a hang, so ffmpeg's own position must never reach 100%.
    [Fact]
    public void FinishedEncodeStillLeavesRoomForTheFaststartPass()
    {
        var entry = new FullSessionFinalizeProgress(
            @"D:\clip.mp4", SessionSeconds: 600, MuxedSeconds: 600, Reencoding: true, StartedUtc: DateTime.UtcNow.AddSeconds(-100));

        Assert.Contains("85%", MainWindowViewModel.DescribeFinalize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroLengthSessionCannotDivideByZero()
    {
        var entry = new FullSessionFinalizeProgress(
            @"D:\clip.mp4", SessionSeconds: 0, MuxedSeconds: 0, Reencoding: false, StartedUtc: DateTime.UtcNow);

        Assert.Contains("Adding session audio", MainWindowViewModel.DescribeFinalize(entry), StringComparison.Ordinal);
    }
}
