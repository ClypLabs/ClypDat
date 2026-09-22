using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GameFrameOwnershipTests
{
    [Theory]
    [InlineData(1366, 768, true)]
    public void DetectorAspectRatioAllowsSupportedLayoutsOnly(int width, int height, bool expected)
    {
        Assert.Equal(expected, NativeReplayBuffer.IsSupportedDetectorAspectRatio(width, height));
    }

    // Banner matching reads thin bright strokes, and a downscaled capture loses
    // those first: the gap between a real banner and empty scenery is wide at
    // 1080p and 1440p but collapses at 720p, where it straddles the threshold in
    // both directions. 16:9 alone is not enough to run the detector on.
    [Theory]
    [InlineData(1920, 1080, true)]
    public void DetectorResolutionRefusesCapturesTooSmallToReadBanners(int width, int height, bool expected)
    {
        Assert.Equal(expected, NativeReplayBuffer.IsSupportedDetectorResolution(width, height));
    }

    [Fact]
    public void CrossDeviceLease_MustCopyBeforeVideoProcessing()
    {
        Assert.False(NativeReplayBuffer.CanUseDirectVideoProcessorInput(
            directBltAvailable: true,
            requiresCopyBeforeProcessing: true,
            textureIsOwnedByCapture: true,
            cursorCompositingActive: false));
        Assert.True(NativeReplayBuffer.CanUseDirectVideoProcessorInput(
            directBltAvailable: true,
            requiresCopyBeforeProcessing: false,
            textureIsOwnedByCapture: true,
            cursorCompositingActive: false));
        Assert.False(NativeReplayBuffer.CanUseDirectVideoProcessorInput(
            directBltAvailable: true,
            requiresCopyBeforeProcessing: false,
            textureIsOwnedByCapture: false,
            cursorCompositingActive: false));
    }

    // The capture thread's direct Blt and the pacing thread's tick share one
    // ID3D11VideoProcessor, and the cursor overlay is per-Blt state on it. Two
    // threads writing that state killed the capture worker inside NVIDIA's
    // D3D11 driver, so whenever the cursor has to be composited the crop copy
    // runs instead and the pacing tick owns the processor alone.
    [Fact]
    public void CursorCompositing_KeepsTheVideoProcessorOnOneThread()
    {
        Assert.False(NativeReplayBuffer.CanUseDirectVideoProcessorInput(
            directBltAvailable: true,
            requiresCopyBeforeProcessing: false,
            textureIsOwnedByCapture: true,
            cursorCompositingActive: true));
    }

    // Alt-tabbing out of an exclusive-fullscreen game minimises it (GLFW does
    // this by default), and a minimised window cannot be scanned - so the held
    // game has to survive on window identity alone, and drop only when the
    // window is gone or its handle has been recycled by another process.
    [Theory]
    [InlineData(true, 4321u, 4321, true)]    // minimised, same process - still the game
    public void HeldGameSurvivesMinimiseButNotWindowDeathOrHandleReuse(bool windowExists, uint windowProcessId, int detectionProcessId, bool expected)
    {
        Assert.Equal(expected, ForegroundGameDetector.IsHeldGameStillRunning(windowExists, windowProcessId, detectionProcessId));
    }

    [Theory]
    [InlineData(3840, 2160, 0, 0, 3840, 2160, true)]
    public void CropMustFitAcquiredTexture(int textureWidth, int textureHeight, int left, int top, int width, int height, bool expected)
    {
        Assert.Equal(expected, NativeReplayBuffer.IsCropWithinTexture(textureWidth, textureHeight, left, top, width, height));
    }

    [Fact]
    public void DxgiAcquireDeadline_CapsCallsToCaptureCadenceAndSnapsAfterAStall()
    {
        var scheduled = TimeSpan.FromSeconds(1);
        Assert.Equal(
            TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(1d / 60),
            NativeReplayBuffer.NextDxgiAcquireDeadline(scheduled, TimeSpan.FromSeconds(1.004), 60));
        Assert.Equal(
            TimeSpan.FromSeconds(1.050),
            NativeReplayBuffer.NextDxgiAcquireDeadline(scheduled, TimeSpan.FromSeconds(1.050), 60));
    }
}
