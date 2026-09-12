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
    [InlineData(1, .1, 10.1)]
    [InlineData(2, .1, 10.2)]
    [InlineData(4, .1, 10.4)]
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
    public void FreezesAfterMissingSampleLimitUntilFreshVlcSample()
    {
        long now = 0;
        var clock = new EditorOverlayClock(() => TimeSpan.FromSeconds(20), () => now);
        clock.Reset(1, TimeSpan.Zero, 2);
        clock.Resume(1, TimeSpan.FromSeconds(10), 2);
        now = Ticks(1);
        Assert.True(clock.TryGetOverlayPosition(out var limited));
        Assert.Equal(11, limited.TotalSeconds, 3);
        now = Ticks(2);
        Assert.True(clock.TryGetOverlayPosition(out var frozen));
        Assert.Equal(limited, frozen);

        clock.Sample(1, TimeSpan.FromSeconds(12.5), now);
        Assert.True(clock.TryGetOverlayPosition(out var refreshed));
        Assert.Equal(12.5, refreshed.TotalSeconds, 3);
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

    [Fact]
    public void DuplicateSamplesAndEndBoundaryStayAtVlcTime()
    {
        long now = 0;
        var clock = new EditorOverlayClock(() => TimeSpan.FromSeconds(10), () => now);
        clock.Reset(1, TimeSpan.Zero, 1);
        clock.Resume(1, TimeSpan.FromSeconds(9.8), 1);
        now = Ticks(.1);
        clock.Sample(1, TimeSpan.FromSeconds(9.8), now);
        now = Ticks(.3);
        Assert.True(clock.TryGetOverlayPosition(out var interpolated));
        Assert.Equal(10, interpolated.TotalSeconds, 3);
        clock.FreezeAtCurrent(1, now);
        now = Ticks(2);
        Assert.True(clock.TryGetOverlayPosition(out var ended));
        Assert.Equal(10, ended.TotalSeconds, 3);
    }

    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);
}
