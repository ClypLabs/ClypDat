using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplayVideoCodecPolicyTests
{
    [Theory]
    [InlineData(null, "H.264")]
    [InlineData("", "H.264")]
    [InlineData("Auto", "H.264")]
    [InlineData("AV1", "AV1")]
    [InlineData("H.264", "H.264")]
    [InlineData("h.264", "H.264")]
    public void Normalize_UsesH264UnlessAv1Preferred(string? requested, string expected) =>
        Assert.Equal(expected, ReplayVideoCodecPolicy.Normalize(requested));

    [Fact]
    public void Candidates_UsesVendorOrderThenH264Fallbacks()
    {
        var candidates = ReplayVideoCodecPolicy.Candidates("AV1", "qsv");

        Assert.Equal(new[] { "av1_nvenc", "av1_amf", "av1_qsv", "h264_nvenc", "h264_amf", "h264_qsv", "libx264" }, candidates);
    }

    [Fact]
    public void Candidates_DoesNotSkipAv1WhenStartupProbeFoundNone()
    {
        var candidates = ReplayVideoCodecPolicy.Candidates("AV1", null);

        Assert.Equal(new[] { "av1_nvenc", "av1_amf", "av1_qsv", "h264_nvenc", "h264_amf", "h264_qsv", "libx264" }, candidates);
    }

    [Fact]
    public void Candidates_H264OverrideSkipsAv1()
    {
        var candidates = ReplayVideoCodecPolicy.Candidates("H.264", "nvenc");

        Assert.Equal(new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" }, candidates);
    }

    [Fact]
    public void Candidates_CpuModeUsesSoftwareH264Only()
    {
        var candidates = ReplayVideoCodecPolicy.Candidates("AV1", "nvenc", "CPU");

        Assert.Equal(new[] { "libx264" }, candidates);
    }

}
