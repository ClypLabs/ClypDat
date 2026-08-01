using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplayEncoderPresetPolicyTests
{
    [Theory]
    [InlineData("P1")]
    [InlineData("P3")]
    [InlineData("P5")]
    public void Resolve_PreservesManualPreset(string preset) =>
        Assert.Equal(preset, ReplayEncoderPresetPolicy.Resolve(preset));

    [Fact]
    public void Resolve_NormalizesInvalidPresetToP4() =>
        Assert.Equal("P4", ReplayEncoderPresetPolicy.Resolve("invalid"));
}
