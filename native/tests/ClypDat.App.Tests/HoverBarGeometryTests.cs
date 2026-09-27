using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class HoverBarGeometryTests
{
    [Theory]
    [InlineData(-1920, 1100, -1320, 1178, -1920, 1100, 600, 78, true)]
    [InlineData(-1920, 1100, -1321, 1178, -1920, 1100, 600, 78, false)]
    [InlineData(-1920, 1100, -1320, 1178, -1920, 1100, 800, 104, false)]
    [InlineData(100, 100, 700, 178, 100, 101, 600, 78, false)]
    public void NativePositionAndSizeMustBothMatch(int left, int top, int right, int bottom,
        int targetLeft, int targetTop, int targetWidth, int targetHeight, bool expected)
    {
        Assert.Equal(expected, HoverBarGeometry.MatchesNative(left, top, right, bottom,
            targetLeft, targetTop, targetWidth, targetHeight));
    }

    [Theory]
    [InlineData(52, 1, 52)]
    [InlineData(52, 1.25, 65)]
    [InlineData(52, 1.5, 78)]
    [InlineData(52, 2, 104)]
    [InlineData(0, 2, 1)]
    public void ConvertsDipsToNativePixels(double dips, double scale, int expected) =>
        Assert.Equal(expected, HoverBarGeometry.PixelSize(dips, scale));

    [Theory]
    [InlineData(false, true, true, false, true, false, false, "no view model")]
    [InlineData(true, false, true, false, true, false, false, "main window hidden")]
    [InlineData(true, true, false, false, true, false, false, "editor hidden")]
    [InlineData(true, true, true, true, true, false, false, "fullscreen")]
    [InlineData(true, true, true, false, false, false, false, "no playback")]
    [InlineData(true, true, true, false, true, true, false, "video loading")]
    [InlineData(true, true, true, false, true, false, true, "covered")]
    [InlineData(true, true, true, false, true, false, false, null)]
    public void ReportsFirstBlockingGate(bool hasViewModel, bool mainVisible, bool editorVisible,
        bool fullscreen, bool hasPlayback, bool loading, bool covered, string? expected) =>
        Assert.Equal(expected, HoverBarEligibility.BlockedReason(hasViewModel, mainVisible, editorVisible,
            fullscreen, hasPlayback, loading, covered));
}
