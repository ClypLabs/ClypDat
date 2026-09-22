using System.Diagnostics;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorOverlayClockTests
{
    [Fact]
    public void RecordedResumeTrace_UsesLatestVlcAnchorInsteadOfUiStopwatch()
    {
        long now = 0;
        var clock = new EditorOverlayClock(() => TimeSpan.FromSeconds(20), () => now);
        clock.Reset(1, TimeSpan.Zero, 1);
        clock.Resume(1, TimeSpan.FromSeconds(10.384), 1);
        now = Ticks(1.056);
        clock.Sample(1, TimeSpan.FromSeconds(11.634), now);

        Assert.True(clock.TryGetOverlayPosition(out var position));
        Assert.Equal(CapturedOverlayPlayback.FrameIndex(11.634, 720), CapturedOverlayPlayback.FrameIndex(position.TotalSeconds, 720));
        Assert.Equal(11.634, position.TotalSeconds, 3);
    }

    [Theory]
    [InlineData(.25, .1, 10.025)]
    public void InterpolatesAtAcceptedPlaybackRate(double rate, double elapsed, double expected)
    {
        long now = 0;
        var clock = new EditorOverlayClock(() => TimeSpan.FromSeconds(20), () => now);
        clock.Reset(1, TimeSpan.Zero, rate);
        clock.Resume(1, TimeSpan.FromSeconds(10), rate);
        now = Ticks(elapsed);

        Assert.True(clock.TryGetOverlayPosition(out var position));
        Assert.Equal(expected, position.TotalSeconds, 3);
    }

    [Fact]
    public void PauseSeekAndStaleCallbackCannotAdvanceNewGeneration()
    {
        long now = 0;
        var clock = new EditorOverlayClock(() => TimeSpan.FromSeconds(20), () => now);
        clock.Reset(1, TimeSpan.Zero, 1);
        clock.Resume(1, TimeSpan.FromSeconds(5), 1);
        now = Ticks(.2);
        clock.Freeze(1, TimeSpan.FromSeconds(5.2));
        now = Ticks(2);
        Assert.True(clock.TryGetOverlayPosition(out var paused));
        Assert.Equal(5.2, paused.TotalSeconds, 3);

        clock.BeginSeek(2, TimeSpan.FromSeconds(12), 1);
        Assert.False(clock.TryGetOverlayPosition(out _));
        clock.Sample(1, TimeSpan.FromSeconds(7), now);
        clock.Resume(2, TimeSpan.FromSeconds(12), 1);
        Assert.True(clock.TryGetOverlayPosition(out var sought));
        Assert.Equal(12, sought.TotalSeconds, 3);
    }

    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);
}
