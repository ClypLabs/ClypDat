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
    public void BuildDecoderArguments_PreservesFractionalFrameRate()
    {
        var arguments = ClipHoverPreviewController.BuildDecoderArguments("clip.mp4", (TimeSpan.Zero, TimeSpan.FromSeconds(5)), 29.97);

        Assert.Contains("fps=29.97,scale=w=1920:h=1080:flags=lanczos:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2", arguments);
        Assert.DoesNotContain("-re", arguments);
        Assert.Contains("-an", arguments);
        Assert.Contains("bgra", arguments);
    }

    [Fact]
    public void PreviewGeometryAndDelay_MatchHighQualityDefaults()
    {
        Assert.Equal(1920, ClipHoverPreviewController.Width);
        Assert.Equal(1080, ClipHoverPreviewController.Height);
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
