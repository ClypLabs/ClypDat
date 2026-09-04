namespace ClypDat.App.Services;

public sealed record GrayDetectorImage(int Width, int Height, byte[] Pixels);

public sealed record DetectorFrameSnapshot(
    DateTime CapturedUtc,
    GrayDetectorImage CenterBanner,
    GrayDetectorImage MissionPanel,
    GrayDetectorImage KillCounter);

public interface IDetectorFrameSource
{
    event EventHandler<DetectorFrameSnapshot>? DetectorFrameAvailable;
}
