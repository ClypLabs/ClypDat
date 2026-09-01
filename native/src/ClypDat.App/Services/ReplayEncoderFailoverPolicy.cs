namespace ClypDat.App.Services;

// Keeps automatic recovery deterministic: a candidate that has already
// saturated during this capture session is not retried until the next session.
// NVENC's D3D11 and upload paths exercise different bottlenecks, so an upload
// overload must still allow the zero-copy path to qualify before AMF/QSV.
internal static class ReplayEncoderFailoverPolicy
{
    internal const int RequiredOverloadWindows = 3;

    internal static int ProtectedFrameRateAfterHardwareExhaustion(int current, bool enabled) =>
        !enabled ? current : current switch
        {
            > 90 => 90,
            > 60 => 60,
            > 30 => 30,
            _ => current
        };

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
            // FFmpeg contexts bind AV_PIX_FMT_D3D11 and AV_PIX_FMT_NV12 at
            // avcodec_open2. Switching between them while pacing and encode
            // threads own queued frames risks native use-after-free.
            .Where(candidate => candidate.IsD3D11 == active.IsD3D11)
            // Automatic GPU capture must remain on hardware. CPU takeover under
            // a foreground game made measured throughput worse than the NVENC
            // startup window it was reacting to. Keep libx264 for an explicitly
            // selected CPU mode, not live GPU recovery.
            .Where(candidate => !active.IsHardware || candidate.IsHardware)
            .Where(candidate => !attempted.Contains(candidate))
            .ToArray();
    }

    internal static bool RequiresWorkerRestartAfterDeviceRebind(
        ReplayEncoderCandidate active, bool rebindSucceeded) =>
        active.IsD3D11 && !rebindSucceeded;
}
