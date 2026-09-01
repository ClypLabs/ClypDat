using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ReplayEncoderQualificationPolicyTests
{
    [Theory]
    [InlineData(ReplayVideoCodecPolicy.H264, "h264_nvenc")]
    [InlineData(ReplayVideoCodecPolicy.Av1, "av1_nvenc")]
    public void StartupCandidates_PreferD3D11Nvenc(string codec, string encoder)
    {
        var first = ReplayEncoderQualificationPolicy.StartupCandidates(codec, 90).First();
        Assert.Equal(encoder, first.Name);
        Assert.Equal(ReplayEncoderInputPath.D3D11, first.InputPath);
    }

    [Fact]
    public void NvencSelection_UsesSystemMemoryOnlyWhenD3D11Unavailable()
    {
        var available = new NvencInputPathQualification.Result(true, 90);
        var unavailable = new NvencInputPathQualification.Result(false, 0);
        Assert.Equal(NvencInputPath.D3D11, NvencInputPathQualification.Select(90, available, available));
        Assert.Equal(NvencInputPath.SystemMemory, NvencInputPathQualification.Select(90, unavailable, available));
    }

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

    [Fact]
    public void DeviceRebindFailure_TriggersImmediateSupervisorRecovery()
    {
        var health = ReplayCaptureHealth.Unknown("Worker") with
        {
            PipelineRecoveryAction = ReplayPipelineRecoveryAction.RestartWorker,
            LastFailure = "D3D11 encoder could not rebind after device recovery; restarting capture worker."
        };

        Assert.True(new CaptureHealthRecoveryPolicy().Observe(health));
    }

    [Fact]
    public void NonNvencOrdering_RemainsAmfThenQsv_AndCpuIsExplicitOnly()
    {
        var gpu = ReplayEncoderQualificationPolicy.StartupCandidates(ReplayVideoCodecPolicy.H264, 90);
        var amfIndex = Array.FindIndex(gpu.ToArray(), candidate => candidate.Name == "h264_amf");
        var qsvIndex = Array.FindIndex(gpu.ToArray(), candidate => candidate.Name == "h264_qsv");
        var cpu = ReplayEncoderQualificationPolicy.StartupCandidates(ReplayVideoCodecPolicy.H264, 90, ReplayVideoCodecPolicy.Cpu);
        Assert.True(amfIndex < qsvIndex);
        Assert.Single(cpu);
        Assert.Equal("libx264", cpu[0].Name);
    }
}
