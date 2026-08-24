using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class NewClipsPresentationPolicyTests
{
    [Theory]
    [InlineData(true, false, false, 1)]
    [InlineData(true, false, true, 2)]
    [InlineData(false, false, false, 0)]
    [InlineData(false, false, true, 0)]
    [InlineData(true, true, false, 0)]
    [InlineData(true, true, true, 0)]
    public void Resolve_OnlyPresentsWhenTheMainWindowCanOwnThePopup(
        bool isWindowVisible,
        bool isWindowMinimized,
        bool isEditorVisible,
        int expected)
    {
        Assert.Equal(expected, (int)NewClipsPresentationPolicy.Resolve(isWindowVisible, isWindowMinimized, isEditorVisible));
    }
}
