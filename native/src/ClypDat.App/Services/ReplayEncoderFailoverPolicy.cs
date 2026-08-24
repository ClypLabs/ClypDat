namespace ClypDat.App.Services;

// Keeps automatic recovery deterministic: a candidate that has already
// saturated during this capture session is not retried until the next session.
// NVENC's D3D11 path is deliberately excluded after a system-memory NVENC
// throughput failure; it is only an open-time fallback for that family.
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
            .Where(candidate => !attempted.Contains(candidate))
            .Where(candidate => !(active.Name.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) &&
                                  active.InputPath == ReplayEncoderInputPath.SystemMemory &&
                                  candidate.Name.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) &&
                                  candidate.InputPath == ReplayEncoderInputPath.D3D11))
            .ToArray();
    }
}
