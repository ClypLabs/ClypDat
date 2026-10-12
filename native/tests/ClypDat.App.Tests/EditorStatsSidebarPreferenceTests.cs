using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorStatsSidebarPreferenceTests
{
    [Fact]
    public void LateStatsActivateOnceWhileMissingOrPendingStatsKeepDefaultInfo()
    {
        var preference = new EditorStatsSidebarPreference();
        preference.Begin("clip.mp4", newVisit: true);
        Assert.False(preference.TryActivate("clip.mp4", editorVisible: true, hasStats: false));
        Assert.True(preference.TryActivate("clip.mp4", editorVisible: true, hasStats: true));
        Assert.False(preference.TryActivate("clip.mp4", editorVisible: true, hasStats: true));
    }

    [Fact]
    public void ClickingAnyTabBeforeStatsArriveKeepsTheManualChoice()
    {
        var preference = new EditorStatsSidebarPreference();
        preference.Begin("clip.mp4", newVisit: true);
        preference.UserSelected();
        Assert.False(preference.TryActivate("clip.mp4", editorVisible: true, hasStats: true));
        preference.Begin("clip.mp4", newVisit: false);
        Assert.False(preference.TryActivate("clip.mp4", editorVisible: true, hasStats: true));
    }

    [Fact]
    public void ReopeningOrChangingClipsRestoresStatsAsTheDefault()
    {
        var preference = new EditorStatsSidebarPreference();
        preference.Begin("clip.mp4", newVisit: true);
        preference.UserSelected();
        preference.Begin("clip.mp4", newVisit: true);
        Assert.True(preference.TryActivate("clip.mp4", editorVisible: true, hasStats: true));
        preference.Begin("next.mp4", newVisit: true);
        Assert.False(preference.TryActivate("clip.mp4", editorVisible: true, hasStats: true));
        Assert.True(preference.TryActivate("next.mp4", editorVisible: true, hasStats: true));
    }

    [Fact]
    public void ClosedEditorAndStaleClipCannotConsumeThePendingDefault()
    {
        var preference = new EditorStatsSidebarPreference();
        preference.Begin("current.mp4", newVisit: true);
        Assert.False(preference.TryActivate("old.mp4", editorVisible: true, hasStats: true));
        Assert.False(preference.TryActivate("current.mp4", editorVisible: false, hasStats: true));
        Assert.True(preference.TryActivate("CURRENT.MP4", editorVisible: true, hasStats: true));
        preference.Begin("current.mp4", newVisit: true);
        preference.Close();
        Assert.False(preference.TryActivate("current.mp4", editorVisible: true, hasStats: true));
    }
}
