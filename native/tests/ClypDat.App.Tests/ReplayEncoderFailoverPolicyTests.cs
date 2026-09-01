using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ReplayEncoderFailoverPolicyTests
{
    [Fact]
    public void CandidatesAfter_HardwareEncoder_DoesNotFallBackToLibx264()
    {
        var active = new ReplayEncoderCandidate(
            "h264_nvenc", ReplayVideoCodecPolicy.H264, ReplayEncoderInputPath.SystemMemory, FamilyRank: 0);

        var candidates = ReplayEncoderFailoverPolicy.CandidatesAfter(
            ReplayVideoCodecPolicy.H264, ReplayVideoCodecPolicy.Gpu, active, new HashSet<ReplayEncoderCandidate> { active });

        Assert.DoesNotContain(candidates, candidate => string.Equals(candidate.Name, "libx264", StringComparison.OrdinalIgnoreCase));
    }
}
