namespace ClypDat.App.Services;

/// <summary>
/// Replay codec normalization and live-encoder candidate ordering.
/// </summary>
public static class ReplayVideoCodecPolicy
{
    public const string H264 = "H.264";
    public const string Av1 = "AV1";
    public const string Gpu = "GPU";
    public const string Cpu = "CPU";

    private static readonly string[] H264Candidates = { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" };
    private static readonly string[] Av1Candidates = { "av1_nvenc", "av1_amf", "av1_qsv" };

    public static string Normalize(string? requestedCodec) =>
        string.Equals(requestedCodec, Av1, StringComparison.OrdinalIgnoreCase) ? Av1 : H264;

    public static IReadOnlyList<string> Candidates(string? requestedCodec, string? av1Family, string? encoderMode = Gpu)
    {
        if (string.Equals(encoderMode, Cpu, StringComparison.OrdinalIgnoreCase)) return new[] { "libx264" };
        if (Normalize(requestedCodec) == H264) return H264Candidates;

        // AV1 is qualified against real foreground frames. The small startup
        // probe only says whether a family exists; it must not suppress the
        // other vendors or make AV1 silently skip its sustained test.
        return Av1Candidates.Concat(H264Candidates).ToArray();
    }

    // UI quality keeps existing H.264 1-51 meaning. AV1 uses 0-63; +12
    // matches existing export policy (H.264 20, AV1 32) at default.
}
