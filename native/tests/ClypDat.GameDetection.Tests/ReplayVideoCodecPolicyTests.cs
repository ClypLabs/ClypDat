using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplayVideoCodecPolicyTests
{
    [Theory]
    [InlineData(null, "Auto")]
    [InlineData("", "Auto")]
    [InlineData("AV1", "Auto")]
    [InlineData("H.264", "H.264")]
    [InlineData("h.264", "H.264")]
    public void Normalize_UsesAutoUnlessH264Override(string? requested, string expected) =>
        Assert.Equal(expected, ReplayVideoCodecPolicy.Normalize(requested));

    [Fact]
    public void Candidates_PrefersDetectedAv1FamilyThenH264Fallbacks()
    {
        var candidates = ReplayVideoCodecPolicy.Candidates("Auto", "qsv");

        Assert.Equal(new[] { "av1_qsv", "av1_nvenc", "av1_amf", "h264_nvenc", "h264_amf", "h264_qsv", "libx264" }, candidates);
    }

    [Fact]
    public void Candidates_SkipsAv1WhenProbeFoundNone()
    {
        var candidates = ReplayVideoCodecPolicy.Candidates("Auto", null);

        Assert.Equal(new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" }, candidates);
    }

    [Fact]
    public void Candidates_H264OverrideSkipsAv1()
    {
        var candidates = ReplayVideoCodecPolicy.Candidates("H.264", "nvenc");

        Assert.Equal(new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" }, candidates);
    }

    [Theory]
    [InlineData(1, 13)]
    [InlineData(20, 32)]
    [InlineData(51, 63)]
    public void Av1Quality_MapsExistingH264Scale(int h264Quality, int expected) =>
        Assert.Equal(expected, ReplayVideoCodecPolicy.Av1Quality(h264Quality));
}
