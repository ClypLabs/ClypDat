using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ReplayEncoderQualificationPolicyTests
{
    [Fact]
    public void CandidatesAfter_D3D11Encoder_NeverChangesFrameType()
    {
        var active = new ReplayEncoderCandidate("h264_nvenc", ReplayVideoCodecPolicy.H264, ReplayEncoderInputPath.D3D11, 0);
        var candidates = ReplayEncoderFailoverPolicy.CandidatesAfter(ReplayVideoCodecPolicy.H264, ReplayVideoCodecPolicy.Gpu, active, new HashSet<ReplayEncoderCandidate> { active });
        Assert.All(candidates, candidate => Assert.Equal(ReplayEncoderInputPath.D3D11, candidate.InputPath));
    }

    [Fact]
    public void DeviceRebindFailure_ForD3D11RequestsWorkerRestart()
    {
        var d3d11 = new ReplayEncoderCandidate("h264_nvenc", ReplayVideoCodecPolicy.H264, ReplayEncoderInputPath.D3D11, 0);
        var systemMemory = new ReplayEncoderCandidate("h264_nvenc", ReplayVideoCodecPolicy.H264, ReplayEncoderInputPath.SystemMemory, 0);
        Assert.True(ReplayEncoderFailoverPolicy.RequiresWorkerRestartAfterDeviceRebind(d3d11, false));
        Assert.False(ReplayEncoderFailoverPolicy.RequiresWorkerRestartAfterDeviceRebind(systemMemory, false));
    }
}
