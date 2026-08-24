namespace ClypDat.App.Services;

internal enum ReplayEncoderInputPath
{
    D3D11,
    SystemMemory,
    LowPower,
    Normal
}

internal readonly record struct ReplayEncoderCandidate(
    string Name,
    string Codec,
    ReplayEncoderInputPath InputPath,
    int FamilyRank)
{
    internal bool IsHardware => !string.Equals(Name, "libx264", StringComparison.OrdinalIgnoreCase);
    internal bool IsD3D11 => InputPath == ReplayEncoderInputPath.D3D11;
}

internal sealed record ReplayEncoderQualificationResult(
    ReplayEncoderCandidate Candidate,
    bool Available,
    bool TimedOut,
    IReadOnlyList<double> WindowFramesPerSecond,
    string RejectionReason = "",
    IReadOnlyList<int>? WindowDroppedFrames = null,
    IReadOnlyList<int>? WindowQueueDepths = null,
    int QueueCapacity = 0)
{
    internal double MinimumWindow => WindowFramesPerSecond.Count == 0 ? 0 : WindowFramesPerSecond.Min();
    internal double MeanWindow => WindowFramesPerSecond.Count == 0 ? 0 : WindowFramesPerSecond.Average();

    internal bool ReachedTarget(int targetFrameRate) =>
        Available && !TimedOut &&
        WindowFramesPerSecond.Count >= ReplayEncoderQualificationPolicy.RequiredWindows &&
        WindowFramesPerSecond.All(rate => rate >= targetFrameRate * ReplayEncoderQualificationPolicy.TargetThreshold) &&
        (WindowDroppedFrames is null || WindowDroppedFrames.All(dropped => dropped == 0)) &&
        (WindowQueueDepths is null || QueueCapacity <= 0 || WindowQueueDepths.All(depth => depth * 4 < QueueCapacity * 3));
}

internal static class ReplayEncoderQualificationPolicy
{
    // Three complete foreground seconds reject the brief idle/menu burst that
    // previously let a saturated encoder leave Arming just before gameplay.
    internal const int RequiredWindows = 3;
    // A selected cadence is a contract. Qualification needs normal scheduling
    // tolerance, but it must not bless a sustained shortfall as "exact".
    internal const double TargetThreshold = 0.99;
    internal const double TieTolerance = 0.03;

    internal static IReadOnlyList<ReplayEncoderCandidate> Candidates(
        string requestedCodec,
        bool includeH264Fallback = true,
        string encoderMode = ReplayVideoCodecPolicy.Gpu)
    {
        var candidates = new List<ReplayEncoderCandidate>();
        if (string.Equals(encoderMode, ReplayVideoCodecPolicy.Cpu, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(new ReplayEncoderCandidate("libx264", ReplayVideoCodecPolicy.H264, ReplayEncoderInputPath.SystemMemory, 3));
            return candidates;
        }
        var codec = ReplayVideoCodecPolicy.Normalize(requestedCodec);
        if (codec == ReplayVideoCodecPolicy.Av1)
        {
            AddHardware(candidates, ReplayVideoCodecPolicy.Av1, "av1_nvenc", 0, ReplayEncoderInputPath.D3D11, ReplayEncoderInputPath.SystemMemory);
            AddHardware(candidates, ReplayVideoCodecPolicy.Av1, "av1_amf", 1, ReplayEncoderInputPath.SystemMemory);
            AddHardware(candidates, ReplayVideoCodecPolicy.Av1, "av1_qsv", 2, ReplayEncoderInputPath.LowPower, ReplayEncoderInputPath.Normal);
            if (!includeH264Fallback) return candidates;
        }

        AddHardware(candidates, ReplayVideoCodecPolicy.H264, "h264_nvenc", 0, ReplayEncoderInputPath.D3D11, ReplayEncoderInputPath.SystemMemory);
        AddHardware(candidates, ReplayVideoCodecPolicy.H264, "h264_amf", 1, ReplayEncoderInputPath.SystemMemory);
        AddHardware(candidates, ReplayVideoCodecPolicy.H264, "h264_qsv", 2, ReplayEncoderInputPath.LowPower, ReplayEncoderInputPath.Normal);
        candidates.Add(new ReplayEncoderCandidate("libx264", ReplayVideoCodecPolicy.H264, ReplayEncoderInputPath.SystemMemory, 3));
        return candidates;
    }

    // The system-memory NVENC upload path is independent of the game's D3D11
    // queue. Prefer it for startup at every supported rate; D3D11 remains an
    // open-time fallback when the independent path is unavailable.
    internal static IReadOnlyList<ReplayEncoderCandidate> StartupCandidates(
        string requestedCodec,
        int targetFrameRate,
        string encoderMode = ReplayVideoCodecPolicy.Gpu) =>
        Candidates(requestedCodec, encoderMode: encoderMode)
            .OrderBy(candidate => candidate.Name.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) &&
                                  candidate.InputPath == ReplayEncoderInputPath.SystemMemory ? 0 :
                                  candidate.InputPath == ReplayEncoderInputPath.D3D11 ? 1 : 2)
            .ToArray();

    internal static ReplayEncoderQualificationResult? Select(
        int targetFrameRate,
        string requestedCodec,
        IReadOnlyList<ReplayEncoderQualificationResult> results)
    {
        var available = results.Where(result => result.Available && !result.TimedOut).ToArray();
        if (available.Length == 0)
            return null;

        var requested = ReplayVideoCodecPolicy.Normalize(requestedCodec);
        var qualifying = available.Where(result => result.ReachedTarget(targetFrameRate)).ToArray();
        if (qualifying.Length > 0)
        {
            // Stay with the requested codec family when it can sustain the
            // target. AV1 only falls through to H.264 when every AV1 family
            // failed its sustained windows.
            var preferredCodec = qualifying.Any(result => result.Candidate.Codec == requested)
                ? requested
                : ReplayVideoCodecPolicy.H264;
            var family = qualifying
                .Where(result => result.Candidate.Codec == preferredCodec)
                .GroupBy(result => result.Candidate.FamilyRank)
                .OrderBy(group => group.Key)
                .FirstOrDefault();
            if (family is not null) return PickWithinFamily(targetFrameRate, family);
        }

        // No candidate met the target. Choose the least-bad sustained result,
        // using the minimum window rather than a startup burst.
        return available
            .OrderByDescending(result => result.MinimumWindow)
            .ThenByDescending(result => result.MeanWindow)
            .ThenBy(result => result.Candidate.FamilyRank)
            .First();
    }

    private static ReplayEncoderQualificationResult PickWithinFamily(
        int targetFrameRate,
        IEnumerable<ReplayEncoderQualificationResult> family)
    {
        // This is deliberately a policy choice rather than a benchmark tie
        // break: D3D11 can look quicker while the desktop is idle, then block
        // on the foreground game's queue in production. If the independent
        // upload path qualified across the complete production windows, use
        // it. D3D11 remains available only when that path cannot qualify.
        var independentNvenc = family.FirstOrDefault(result =>
            result.Candidate.Name.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) &&
            result.Candidate.InputPath == ReplayEncoderInputPath.SystemMemory);
        if (independentNvenc is not null) return independentNvenc;

        var ordered = family
            .OrderByDescending(result => result.MeanWindow)
            .ThenByDescending(result => result.Candidate.IsHardware)
            .ToArray();
        var bestMean = ordered[0].MeanWindow;
        var tied = ordered.Where(result => result.MeanWindow >= bestMean * (1 - TieTolerance));
        // A D3D11 NVENC input surface is not automatically the cheaper path.
        // On the 4070 Ti it is the path that blocks behind the
        // foreground game's D3D work (8-17ms send_frame), while NVENC fed from
        // system memory remains well above target. The old tie-break treated a
        // synthetic, static-frame burst as proof that D3D11 was faster and
        // selected the path that later saved 78 FPS clips. Keep D3D11 only
        // when system memory did not qualify; within the tie band, prefer the
        // independent NVENC upload path at every supported replay rate.
        return tied
            .OrderByDescending(result => result.Candidate.IsHardware)
            .ThenByDescending(result => result.Candidate.IsD3D11)
            .ThenByDescending(result => result.MinimumWindow)
            .First();
    }

    private static void AddHardware(
        ICollection<ReplayEncoderCandidate> candidates,
        string codec,
        string name,
        int familyRank,
        params ReplayEncoderInputPath[] paths)
    {
        foreach (var path in paths)
            candidates.Add(new ReplayEncoderCandidate(name, codec, path, familyRank));
    }
}
