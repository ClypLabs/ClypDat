namespace ClypDat.App.Services;

/// <summary>
/// One recorder profile instead of exposing vendor-specific quality presets.
/// It preserves enough queued NVENC surfaces to keep the hardware encoder
/// pipelined at every supported replay frame rate.
/// </summary>
public static class ReplayEncoderProfilePolicy
{
    public const string Automatic = "Automatic";

    public static string Resolve() => Automatic;

    public static int NvencSurfaces(int frameRate) => Math.Clamp(
        ReplayFrameTimingPolicy.EncodeQueueCapacity(frameRate) + 8,
        16,
        24);
}
