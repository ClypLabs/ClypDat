using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CameraPreviewModeTests
{
    [Fact]
    public void PrefersSmallestNv12SixtyFpsModeCoveringPreview()
    {
        var modes = CameraPreviewModeProbe.Parse("""
            [dshow @ 000] pixel_format=yuyv422  min s=640x360 fps=5 max s=1920x1080 fps=60
            [dshow @ 000] pixel_format=nv12 min s=640x360 fps=5 max s=640x360 fps=60
            [dshow @ 000] vcodec=mjpeg min s=640x360 fps=5 max s=640x360 fps=60
            """);
        Assert.Equal(new CameraPreviewMode(640, 360, 60, "nv12", false), modes[0]);
    }

    [Fact]
    public void FallsBackFromUnsupportedSixtyToThirtyThenLowerRates()
    {
        var modes = CameraPreviewModeProbe.Parse("""
            [dshow @ 000] pixel_format=nv12 min s=640x360 fps=5 max s=640x360 fps=29.97
            [dshow @ 000] pixel_format=nv12 min s=640x360 fps=5 max s=640x360 fps=25
            """);
        Assert.Equal(29.97, modes[0].FramesPerSecond, 2);
        Assert.Equal(25, modes[1].FramesPerSecond);
    }

    [Fact]
    public void UsesArgumentListSafeUnicodeDeviceNameAndPassthroughTiming()
    {
        var arguments = CameraPreviewService.BuildArguments("Cámara Ø", new CameraPreviewMode(640, 360, 59.94, "nv12", false));
        Assert.Contains("video=Cámara Ø", arguments);
        Assert.Contains("59.94", arguments);
        Assert.Contains("-fps_mode", arguments);
        Assert.DoesNotContain("-r", arguments);
    }
}
