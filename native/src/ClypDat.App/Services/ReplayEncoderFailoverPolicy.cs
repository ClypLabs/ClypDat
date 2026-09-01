namespace ClypDat.App.Services;

// Keeps automatic recovery deterministic: a candidate that has already
// saturated during this capture session is not retried until the next session.
// NVENC's D3D11 and upload paths exercise different bottlenecks, so an upload
// overload must still allow the zero-copy path to qualify before AMF/QSV.
internal static class ReplayEncoderFailoverPolicy
{
    internal const int RequiredOverloadWindows = 3;

    // Startup deliberately prefers NVENC's independent upload path. Keep a
    // ready D3D11 pool alive during that choice so an upload-path overload can
    // immediately fail over to NVENC zero-copy instead of exhausting hardware
    // candidates and landing on libx264.
    internal static bool ShouldRetainD3D11FramePool(string encoderMode, bool d3d11FramePoolReady) =>
        d3d11FramePoolReady && !string.Equals(encoderMode, ReplayVideoCodecPolicy.Cpu, StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<ReplayEncoderCandidate> CandidatesAfter(
        string requestedCodec,
        string encoderMode,
        ReplayEncoderCandidate active,
        ISet<ReplayEncoderCandidate> attempted)
    {
        var candidates = ReplayEncoderQualificationPolicy.StartupCandidates(requestedCodec, 0, encoderMode);
        var activeIndex = candidates.ToList().FindIndex(candidate => candidate.Equals(active));
        if (activeIndex < 0) activeIndex = -1;

        return candidates
            .Skip(activeIndex + 1)
            .Where(candidate => candidate.Codec == active.Codec)
            // GPU mode must remain on GPU. A CPU encoder can make capture look
            // alive while its upload/encode work destroys fresh-frame cadence.
            // Keep current hardware context if every hardware replacement
            // fails; its live health remains actionable and no libx264 takeover
            // can turn a GPU overload into a worse CPU bottleneck.
            .Where(candidate => !active.IsHardware || candidate.IsHardware)
            .Where(candidate => !attempted.Contains(candidate))
            .ToArray();
    }
}
