using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ClipMetadataTaggerTests
{
    [Fact]
    public void BuildCommentValue_UsesClypDatNamespace()
    {
        Assert.Equal("CLYPDAT_CAPTURE_BACKEND=ClypDat", ClipMetadataTagger.BuildCommentValue("ClypDat"));
    }

    [Theory]
    [InlineData("Native", "ClypDat")]
    [InlineData("EVE Native", "ClypDat")]
    [InlineData("Windows Capture", "Windows Capture")]
    public void NormalizeBackendLabel_PreservesDisplayCompatibility(string value, string expected) =>
        Assert.Equal(expected, ClipMetadataTagger.NormalizeBackendLabel(value));
}
