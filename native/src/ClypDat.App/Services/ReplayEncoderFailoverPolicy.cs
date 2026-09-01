namespace ClypDat.App.Services;

// Keeps automatic recovery deterministic: a candidate that has already
// saturated during this capture session is not retried until the next session.
// NVENC's D3D11 and upload paths exercise different bottlenecks, so an upload
// overload must still allow the zero-copy path to qualify before AMF/QSV.
internal static class ReplayEncoderFailoverPolicy
{
    internal const int RequiredOverloadWindows = 3;

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
            // Reliability wins after sustained, measured congestion. libx264
            // remains last in startup order, after every same-codec hardware
            // adapter, and generation-tagged packets keep a live swap safe for
            // replay-window remuxing.
            .Where(candidate => !attempted.Contains(candidate))
            .ToArray();
    }
}
