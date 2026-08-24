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

    /// <summary>
    /// Limits every replay GOP to one second at the effective recorder frame rate.
    /// </summary>
    public static int GopFrames(int frameRate) => Math.Clamp(frameRate, 30, 120);

    public static int ReplayQueueCapacity(int frameRate) =>
        ReplayFrameTimingPolicy.EncodeQueueCapacity(frameRate);

    public static int NvencSurfaces(int frameRate) => Math.Clamp(
        (int)Math.Ceiling(Math.Clamp(frameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate) / 2.0),
        16,
        60);

    public static int D3D11FixedPoolSize(int frameRate, int headroom = 4) =>
        NvencSurfaces(frameRate) + ReplayFrameTimingPolicy.EncodeQueueCapacity(frameRate) + 1 + Math.Max(0, headroom);

    public static int D3D11DynamicPoolMinimum(int frameRate, int headroom = 4) =>
        D3D11FixedPoolSize(frameRate, headroom);
}
