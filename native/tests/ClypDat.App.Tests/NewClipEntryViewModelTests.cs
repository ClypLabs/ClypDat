using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NewClipEntryViewModelTests
{
    [Fact]
    public void LoneEntry_CheckboxFollowsHoverAndSelection()
    {
        var entry = new NewClipEntryViewModel(new ClipCardViewModel(
            new MediaFileInfo("clip", "C:\\test\\clip.mp4", DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(1), 1, string.Empty, Array.Empty<MediaTrackInfo>(), 1, 1, 60),
            "C:\\test"));

        Assert.True(entry.ShowCheckBox);
        Assert.False(entry.IsCheckVisible);

        entry.IsHovered = true;
        Assert.True(entry.IsCheckVisible);

        entry.IsHovered = false;
        Assert.False(entry.IsCheckVisible);

        entry.IsSelected = true;
        Assert.True(entry.IsCheckVisible);

        entry.IsHovered = true;
        entry.IsHovered = false;
        Assert.True(entry.IsCheckVisible);

        Assert.False(entry.HasSelectionOrder);
        entry.SelectionOrder = 1;
        Assert.True(entry.HasSelectionOrder);
    }
}
