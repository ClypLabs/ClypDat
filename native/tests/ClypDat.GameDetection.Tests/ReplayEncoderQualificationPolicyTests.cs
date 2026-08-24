using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplayEncoderQualificationPolicyTests
{
    [Fact]
    public void StartupCandidates_PreferIndependentNvencUploadAtHighFrameRates()
    {
        var candidates = ReplayEncoderQualificationPolicy.StartupCandidates("H.264", 120);

        Assert.Equal(new ReplayEncoderCandidate("h264_nvenc", ReplayVideoCodecPolicy.H264, ReplayEncoderInputPath.SystemMemory, 0), candidates[0]);
    }

    [Fact]
    public void Candidates_OrdersH264PathsAndQsvModes()
    {
        var candidates = ReplayEncoderQualificationPolicy.Candidates("H.264");

        Assert.Equal(
            new[] { "h264_nvenc:D3D11", "h264_nvenc:SystemMemory", "h264_amf:SystemMemory", "h264_qsv:LowPower", "h264_qsv:Normal", "libx264:SystemMemory" },
            candidates.Select(candidate => $"{candidate.Name}:{candidate.InputPath}"));
    }

    [Fact]
    public void Select_UsesStableTargetWinnerAndPrefersNvencSystemMemoryAtHighFrameRate()
    {
        var candidates = ReplayEncoderQualificationPolicy.Candidates("H.264");
        var results = new[]
        {
            Result(candidates[0], 120, 119, 118),
            Result(candidates[1], 121, 120, 119),
            Result(candidates[2], 140, 140, 140)
        };

        var selected = ReplayEncoderQualificationPolicy.Select(120, "H.264", results);

        Assert.NotNull(selected);
        Assert.Equal(ReplayEncoderInputPath.SystemMemory, selected!.Candidate.InputPath);
    }

    [Fact]
    public void Select_KeepsD3D11PreferenceBelowHighFrameRateThreshold()
    {
        var candidates = ReplayEncoderQualificationPolicy.Candidates("H.264");
        var selected = ReplayEncoderQualificationPolicy.Select(60, "H.264", new[]
        {
            Result(candidates[0], 60, 59, 59),
            Result(candidates[1], 61, 60, 60)
        });

        Assert.Equal(ReplayEncoderInputPath.D3D11, selected!.Candidate.InputPath);
    }

    [Fact]
    public void Select_ChoosesHardwareBeforeCpuWhenMeansAreTied()
    {
        var candidates = ReplayEncoderQualificationPolicy.Candidates("H.264");
        var selected = ReplayEncoderQualificationPolicy.Select(120, "H.264", new[]
        {
            Result(candidates[2], 120, 120, 120),
            Result(candidates[^1], 120, 120, 120)
        });

        Assert.Equal("h264_amf", selected!.Candidate.Name);
    }

    [Fact]
    public void Select_AllowsAmfAndQsvToWinWhenEarlierFamiliesAreUnavailable()
    {
        var candidates = ReplayEncoderQualificationPolicy.Candidates("H.264");
        var selectedAmf = ReplayEncoderQualificationPolicy.Select(120, "H.264", new[]
        {
            Unavailable(candidates[0]),
            Unavailable(candidates[1]),
            Result(candidates[2], 118, 119, 120)
        });
        var selectedQsv = ReplayEncoderQualificationPolicy.Select(120, "H.264", new[]
        {
            Unavailable(candidates[0]),
            Unavailable(candidates[1]),
            Unavailable(candidates[2]),
            Result(candidates[3], 118, 119, 120)
        });

        Assert.Equal("h264_amf", selectedAmf!.Candidate.Name);
        Assert.Equal(ReplayEncoderInputPath.LowPower, selectedQsv!.Candidate.InputPath);
    }

    [Fact]
    public void Candidates_CpuModeContainsOnlyH264Software()
    {
        var candidates = ReplayEncoderQualificationPolicy.Candidates("AV1", encoderMode: ReplayVideoCodecPolicy.Cpu);

        var candidate = Assert.Single(candidates);
        Assert.Equal("libx264", candidate.Name);
        Assert.Equal(ReplayVideoCodecPolicy.H264, candidate.Codec);
    }

    [Fact]
    public void Select_AllBelowTargetUsesHighestMinimumWindow()
    {
        var candidates = ReplayEncoderQualificationPolicy.Candidates("H.264");
        var selected = ReplayEncoderQualificationPolicy.Select(120, "H.264", new[]
        {
            Result(candidates[0], 113, 112, 111),
            Result(candidates[2], 110, 114, 112)
        });

        Assert.Equal("h264_nvenc", selected!.Candidate.Name);
        Assert.Equal(111, selected.MinimumWindow);
    }

    [Fact]
    public void Select_FallsBackFromAv1ToH264OnlyAfterAv1FailsTarget()
    {
        var candidates = ReplayEncoderQualificationPolicy.Candidates("AV1");
        var selected = ReplayEncoderQualificationPolicy.Select(120, "AV1", new[]
        {
            Result(candidates[0], 80, 80, 80),
            Result(candidates[5], 120, 119, 120)
        });

        Assert.Equal("h264_nvenc", selected!.Candidate.Name);
    }

    [Fact]
    public void Select_UsesMinimumWindowWhenStartupPeakIsMisleading()
    {
        var candidates = ReplayEncoderQualificationPolicy.Candidates("H.264");
        var selected = ReplayEncoderQualificationPolicy.Select(120, "H.264", new[]
        {
            Result(candidates[0], 120, 120, 80),
            Result(candidates[2], 114, 114, 114)
        });

        Assert.Equal("h264_amf", selected!.Candidate.Name);
    }

    [Fact]
    public void Select_RejectsUnavailableAndTimedOutCandidates()
    {
        var candidates = ReplayEncoderQualificationPolicy.Candidates("H.264");
        var selected = ReplayEncoderQualificationPolicy.Select(120, "H.264", new[]
        {
            new ReplayEncoderQualificationResult(candidates[0], false, false, Array.Empty<double>(), "unavailable"),
            new ReplayEncoderQualificationResult(candidates[1], true, true, new[] { 200d, 200d, 200d }, "timeout"),
            Result(candidates[^1], 110, 110, 110)
        });

        Assert.Equal("libx264", selected!.Candidate.Name);
    }

    [Fact]
    public void Result_RequiresThreeWindowsAndNinetyFivePercentInEveryWindow()
    {
        var candidate = ReplayEncoderQualificationPolicy.Candidates("H.264")[0];
        Assert.False(Result(candidate, 120, 120).ReachedTarget(120));
        Assert.False(Result(candidate, 120, 113, 120).ReachedTarget(120));
        Assert.True(Result(candidate, 114, 114, 114).ReachedTarget(120));
    }

    [Fact]
    public void Result_RejectsQueueSaturationEvenWhenOutputRateLooksHealthy()
    {
        var candidate = ReplayEncoderQualificationPolicy.Candidates("H.264")[0];
        var result = new ReplayEncoderQualificationResult(candidate, true, false, new[] { 120d, 120d, 120d },
            WindowDroppedFrames: new[] { 0, 0, 0 }, WindowQueueDepths: new[] { 30, 30, 30 }, QueueCapacity: 30);

        Assert.False(result.ReachedTarget(120));
    }

    [Fact]
    public void Result_RejectsDroppedFramesInAnyProductionWindow()
    {
        var candidate = ReplayEncoderQualificationPolicy.Candidates("H.264")[0];
        var result = new ReplayEncoderQualificationResult(candidate, true, false, new[] { 120d, 120d, 120d },
            WindowDroppedFrames: new[] { 0, 1, 0 });

        Assert.False(result.ReachedTarget(120));
    }

    private static ReplayEncoderQualificationResult Result(ReplayEncoderCandidate candidate, params double[] windows) =>
        new(candidate, true, false, windows);

    private static ReplayEncoderQualificationResult Unavailable(ReplayEncoderCandidate candidate) =>
        new(candidate, false, false, Array.Empty<double>(), "unavailable");
}
