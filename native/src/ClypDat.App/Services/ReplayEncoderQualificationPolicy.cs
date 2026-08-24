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
    string RejectionReason = "")
{
    internal double MinimumWindow => WindowFramesPerSecond.Count == 0 ? 0 : WindowFramesPerSecond.Min();
    internal double MeanWindow => WindowFramesPerSecond.Count == 0 ? 0 : WindowFramesPerSecond.Average();

    internal bool ReachedTarget(int targetFrameRate) =>
        Available && !TimedOut &&
        WindowFramesPerSecond.Count >= ReplayEncoderQualificationPolicy.RequiredWindows &&
        WindowFramesPerSecond.All(rate => rate >= targetFrameRate * ReplayEncoderQualificationPolicy.TargetThreshold);
}

internal static class ReplayEncoderQualificationPolicy
{
    internal const int RequiredWindows = 3;
    internal const double TargetThreshold = 0.95;
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
            if (family is not null) return PickWithinFamily(family);
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
        IEnumerable<ReplayEncoderQualificationResult> family)
    {
        var ordered = family
            .OrderByDescending(result => result.MeanWindow)
            .ThenByDescending(result => result.Candidate.IsHardware)
            .ToArray();
        var bestMean = ordered[0].MeanWindow;
        var tied = ordered.Where(result => result.MeanWindow >= bestMean * (1 - TieTolerance));
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
