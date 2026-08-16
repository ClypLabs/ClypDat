namespace ClypDat.App.Services;

/// <summary>
/// Replay codec normalization and live-encoder candidate ordering.
/// </summary>
public static class ReplayVideoCodecPolicy
{
    public const string Auto = "Auto";
    public const string H264 = "H.264";

    private static readonly string[] H264Candidates = { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" };
    private static readonly string[] Av1Candidates = { "av1_nvenc", "av1_amf", "av1_qsv" };

    public static string Normalize(string? requestedCodec) =>
        string.Equals(requestedCodec, H264, StringComparison.OrdinalIgnoreCase) ? H264 : Auto;

    public static IReadOnlyList<string> Candidates(string? requestedCodec, string? av1Family)
    {
        if (Normalize(requestedCodec) == H264 || string.IsNullOrWhiteSpace(av1Family)) return H264Candidates;

        var preferred = $"av1_{av1Family}";
        return Av1Candidates
            .OrderBy(candidate => string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Concat(H264Candidates)
            .ToArray();
    }

    // UI quality keeps existing H.264 1-51 meaning. AV1 uses 0-63; +12
    // matches existing export policy (H.264 20, AV1 32) at default.
}
