using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ReplayEncoderFailoverPolicyTests
{
    [Theory]
    [InlineData(120, true, 90)]
    [InlineData(90, true, 60)]
    [InlineData(60, true, 30)]
    [InlineData(30, true, 30)]
    [InlineData(60, false, 60)]
    public void HardwareExhaustion_ProtectsCadenceWithoutSwitchingToCpu(int current, bool enabled, int expected)
    {
        Assert.Equal(expected, ReplayEncoderFailoverPolicy.ProtectedFrameRateAfterHardwareExhaustion(current, enabled));
    }

    [Fact]
    public void CandidatesAfter_HardwareEncoder_IncludesLibx264AsLastResort()
    {
        var active = new ReplayEncoderCandidate(
            "h264_nvenc", ReplayVideoCodecPolicy.H264, ReplayEncoderInputPath.SystemMemory, FamilyRank: 0);

        var candidates = ReplayEncoderFailoverPolicy.CandidatesAfter(
            ReplayVideoCodecPolicy.H264, ReplayVideoCodecPolicy.Gpu, active, new HashSet<ReplayEncoderCandidate> { active });

        Assert.Contains(candidates, candidate => string.Equals(candidate.Name, "libx264", StringComparison.OrdinalIgnoreCase));
    }
}
