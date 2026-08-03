using Avalonia;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ClipHoverPreviewControllerTests
{
    [Theory]
    [InlineData(24, 24)]
    [InlineData(29.97, 29.97)]
    [InlineData(59.94, 59.94)]
    [InlineData(60, 60)]
    [InlineData(120, 60)]
    [InlineData(240, 60)]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(double.NaN, 30)]
    [InlineData(double.PositiveInfinity, 30)]
    public void ResolveFrameRate_UsesRecordedRateWithinSafeBounds(double recorded, double expected)
    {
        Assert.Equal(expected, ClipHoverPreviewController.ResolveFrameRate(recorded));
    }

    [Fact]
    public void BuildDecoderArguments_PreservesFractionalFrameRateAndScalesToCard()
    {
        var arguments = ClipHoverPreviewController.BuildDecoderArguments(
            "clip.mp4", (TimeSpan.Zero, TimeSpan.FromSeconds(5)), 29.97, new PixelSize(480, 270));

        Assert.Contains("fps=29.97,scale=w=480:h=270:flags=bilinear:force_original_aspect_ratio=decrease,pad=480:270:(ow-iw)/2:(oh-ih)/2", arguments);
        Assert.DoesNotContain("-re", arguments);
        Assert.Contains("-an", arguments);
        Assert.Contains("bgra", arguments);
    }

    [Theory]
    // Card DIP size at 1x scaling passes through, rounded up to even.
    [InlineData(320, 180, 1.0, 320, 180)]
    [InlineData(220, 123, 1.0, 220, 124)]
    // Render scaling is applied, so a HiDPI card decodes at its real pixels.
    [InlineData(320, 180, 1.5, 480, 270)]
    // ...until the long-edge cap kicks in, which scales height to match.
    [InlineData(800, 450, 1.0, 640, 360)]
    [InlineData(640, 360, 2.0, 640, 360)]
    // An unmeasured card (not laid out yet) falls back to the default size.
    [InlineData(0, 0, 1.0, ClipHoverPreviewController.DefaultPreviewWidth, ClipHoverPreviewController.DefaultPreviewHeight)]
    public void ResolvePreviewSize_TracksCardPixelsWithinBounds(double width, double height, double scaling, int expectedWidth, int expectedHeight)
    {
        var size = ClipHoverPreviewController.ResolvePreviewSize(new Size(width, height), scaling);

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void ResolvePreviewSize_TreatsUnusableScalingAsOneToOne(double scaling)
    {
        var size = ClipHoverPreviewController.ResolvePreviewSize(new Size(320, 180), scaling);

        Assert.Equal(320, size.Width);
        Assert.Equal(180, size.Height);
    }

    [Fact]
    public void ResolvePreviewSize_NeverGoesBelowTheMinimumOrExceedsTheCap()
    {
        var tiny = ClipHoverPreviewController.ResolvePreviewSize(new Size(4, 2), 1.0);
        Assert.Equal(160, tiny.Width);
        Assert.Equal(90, tiny.Height);

        var huge = ClipHoverPreviewController.ResolvePreviewSize(new Size(1920, 1080), 2.0);
        Assert.Equal(ClipHoverPreviewController.MaximumPreviewWidth, huge.Width);
        Assert.True(huge.Height <= 360);
    }

    [Fact]
    public void PreviewDelaysMatchHighQualityDefaults()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(75), ClipHoverPreviewController.HoverDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(150), ClipHoverPreviewController.WarmExitGrace);
    }

    [Fact]
    public void FramePacer_PresentsFirstFrameImmediatelyThenUsesRecordedInterval()
    {
        var pacer = new HoverPreviewFramePacer(60);

        Assert.Equal(TimeSpan.Zero, pacer.NextDelay(TimeSpan.FromMilliseconds(100)));
        Assert.InRange(pacer.NextDelay(TimeSpan.FromMilliseconds(100)).TotalMilliseconds, 16.65, 16.68);
    }

    [Fact]
    public void FramePacer_ReanchorsAfterLateFrameInsteadOfBursting()
    {
        var pacer = new HoverPreviewFramePacer(60);
        pacer.NextDelay(TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, pacer.NextDelay(TimeSpan.FromMilliseconds(40)));
        Assert.InRange(pacer.NextDelay(TimeSpan.FromMilliseconds(40)).TotalMilliseconds, 16.65, 16.68);
    }

    [Fact]
    public void FramePacer_ResetPresentsReattachedFrameImmediately()
    {
        var pacer = new HoverPreviewFramePacer(60);
        pacer.NextDelay(TimeSpan.Zero);
        pacer.Reset();

        Assert.Equal(TimeSpan.Zero, pacer.NextDelay(TimeSpan.FromSeconds(1)));
    }

    // A machine with decode headroom: 4ms to produce a frame, well inside a
    // 60fps budget.
    private const double FastMachineMs = 4;
    // A machine that needs 40ms per frame - 25fps, nowhere near a 60fps
    // request, but comfortable at 30.
    private const double SlowMachineMs = 40;

    [Fact]
    public void FramePacer_StaysAtFullRateWhenTheMachineKeepsUp()
    {
        var pacer = new HoverPreviewFramePacer(60);

        Run(pacer, FastMachineMs, frames: 600);

        Assert.False(pacer.IsReduced);
        Assert.Equal(60, pacer.CurrentFrameRate);
        Assert.False(pacer.TryConsumeRateChange(out _));
    }

    [Fact]
    public void FramePacer_DropsToReducedRateWhenItCannotSustainFullRate()
    {
        var pacer = new HoverPreviewFramePacer(60);

        Run(pacer, SlowMachineMs, frames: 120);

        Assert.True(pacer.IsReduced);
        Assert.Equal(ClipHoverPreviewController.ReducedFramesPerSecond, pacer.CurrentFrameRate);
        Assert.True(pacer.TryConsumeRateChange(out var rate));
        Assert.Equal(ClipHoverPreviewController.ReducedFramesPerSecond, rate);
        // Reported once per transition, not once per observation.
        Assert.False(pacer.TryConsumeRateChange(out _));
    }

    [Fact]
    public void FramePacer_RestoresFullRateOnlyAfterSeveralCleanWindows()
    {
        var pacer = new HoverPreviewFramePacer(60);
        Run(pacer, SlowMachineMs, frames: 120);
        Assert.True(pacer.IsReduced);
        pacer.TryConsumeRateChange(out _);

        // One clean window is not enough to hand full rate back.
        Run(pacer, FastMachineMs, frames: 70);
        Assert.True(pacer.IsReduced);

        // Sustained headroom eventually earns it back.
        Run(pacer, FastMachineMs, frames: 400);
        Assert.False(pacer.IsReduced);
        Assert.Equal(60, pacer.CurrentFrameRate);
        Assert.True(pacer.TryConsumeRateChange(out var rate));
        Assert.Equal(60, rate);
    }

    [Fact]
    public void FramePacer_StopsOfferingFullRateBackAfterRepeatedOverload()
    {
        var pacer = new HoverPreviewFramePacer(60);

        // Degrade, recover, degrade again - then never restore, however clean
        // it looks afterwards.
        Run(pacer, SlowMachineMs, frames: 120);
        Assert.True(pacer.IsReduced);
        Run(pacer, FastMachineMs, frames: 500);
        Assert.False(pacer.IsReduced);
        Run(pacer, SlowMachineMs, frames: 200);
        Assert.True(pacer.IsReduced);

        Run(pacer, FastMachineMs, frames: 3000);

        Assert.True(pacer.IsReduced);
        Assert.Equal(ClipHoverPreviewController.ReducedFramesPerSecond, pacer.CurrentFrameRate);
    }

    [Fact]
    public void FramePacer_NeverDegradesAClipAlreadyAtOrBelowTheReducedRate()
    {
        var pacer = new HoverPreviewFramePacer(24);

        Run(pacer, serviceMs: 200, frames: 600);

        Assert.False(pacer.IsReduced);
        Assert.Equal(24, pacer.CurrentFrameRate);
    }

    [Fact]
    public void FramePacer_IgnoresTimeSpentDetachedWhenJudgingTheMachine()
    {
        var pacer = new HoverPreviewFramePacer(60);
        var clock = Run(pacer, FastMachineMs, frames: 100);

        // Pointer left the card for a while, then came back. Every frame in
        // that gap was "missed", but none of it says the machine is slow.
        pacer.Reset();
        Run(pacer, FastMachineMs, frames: 400, startAt: clock + TimeSpan.FromMinutes(2));

        Assert.False(pacer.IsReduced);
    }

    // Drives the pacer the way ProduceFramesAsync does - honour the delay it
    // asks for, then spend serviceMs producing the frame - and returns the
    // clock it ended on so a caller can continue from there.
    private static TimeSpan Run(HoverPreviewFramePacer pacer, double serviceMs, int frames, TimeSpan startAt = default)
    {
        var now = startAt;
        var service = TimeSpan.FromMilliseconds(serviceMs);
        for (var i = 0; i < frames; i++)
        {
            now += pacer.NextDelay(now) + service;
        }
        return now;
    }

    [Fact]
    public async Task LatestFrameMailbox_ReplacesStalePendingFrame()
    {
        var mailbox = new LatestFrameMailbox<string>();

        Assert.Null(mailbox.Publish("old"));
        Assert.Equal("old", mailbox.Publish("new"));
        Assert.Equal("new", await mailbox.ReadAsync(CancellationToken.None));

        mailbox.Complete();
        Assert.Null(await mailbox.ReadAsync(CancellationToken.None));
    }
}
