namespace ClypDat.Capture.Abstractions;

public static class DetectorHostProtocol
{
    public const int Version = 1;
    public const string SharedMemoryPrefix = "ClypDat-DetectorFrames-";
    public const string PipePrefix = "ClypDat-DetectorHost-";
    public const int FrameSlotCount = 3;
    public const int NormalizedWidth = 1920;
    public const int NormalizedHeight = 1080;
    public const int MaximumFramesPerSecond = 10;
    public const long MaximumWorkingSetBytes = 512L * 1024 * 1024;
}
