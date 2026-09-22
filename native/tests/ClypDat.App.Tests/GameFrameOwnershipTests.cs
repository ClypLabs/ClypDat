using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GameFrameOwnershipTests
{
    [Theory]
    [InlineData(1366, 768, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(2560, 1440, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(3440, 1440, false)]
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
    [InlineData(2560, 1440, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(1280, 720, false)]
    [InlineData(1366, 768, false)]
    [InlineData(3440, 1440, false)]
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
    [InlineData(false, 4321u, 4321, false)]  // window destroyed - game closed
    [InlineData(true, 9999u, 4321, false)]   // handle recycled by another process
    [InlineData(true, 0u, 4321, false)]      // no owning process
    public void HeldGameSurvivesMinimiseButNotWindowDeathOrHandleReuse(bool windowExists, uint windowProcessId, int detectionProcessId, bool expected)
    {
        Assert.Equal(expected, ForegroundGameDetector.IsHeldGameStillRunning(windowExists, windowProcessId, detectionProcessId));
    }

    [Theory]
    [InlineData(3840, 2160, 0, 0, 3840, 2160, true)]
    [InlineData(3840, 2160, 3200, 0, 640, 2160, true)]
    [InlineData(3840, 2160, 3200, 0, 641, 2160, false)]
    [InlineData(3840, 2160, -1, 0, 1920, 1080, false)]
    [InlineData(3840, 2160, 0, 0, 0, 1080, false)]
    public void CropMustFitAcquiredTexture(int textureWidth, int textureHeight, int left, int top, int width, int height, bool expected)
    {
        Assert.Equal(expected, NativeReplayBuffer.IsCropWithinTexture(textureWidth, textureHeight, left, top, width, height));
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080, true)]
    [InlineData(1920, 1080, 3840, 2160, false)]
    [InlineData(0, 1080, 0, 1080, false)]
    public void WgcPublishesOnlyWhenContentMatchesSurface(int contentWidth, int contentHeight, int surfaceWidth, int surfaceHeight, bool expected)
    {
        Assert.Equal(expected, WindowGraphicsCaptureSource.CanPublishFrame(contentWidth, contentHeight, surfaceWidth, surfaceHeight));
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
