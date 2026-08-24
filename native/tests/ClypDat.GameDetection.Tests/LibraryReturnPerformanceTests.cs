using Avalonia;
using Avalonia.Controls;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class LibraryReturnPerformanceTests
{
    [Fact]
    public void RetainedHost_DeactivationKeepsChildMeasureAndLayout()
    {
        var probe = new MeasureProbe();
        var host = new RetainedPageHost { Child = probe };
        host.Measure(new Size(400, 300));
        host.Arrange(new Rect(0, 0, 400, 300));
        var measureCount = probe.MeasureCount;
        var bounds = probe.Bounds;

        host.IsActive = false;
        host.Measure(new Size(400, 300));
        host.Arrange(new Rect(0, 0, 400, 300));

        Assert.Equal(measureCount, probe.MeasureCount);
        Assert.Equal(bounds, probe.Bounds);
        Assert.Equal(0, host.Opacity);
        Assert.False(host.IsHitTestVisible);
        Assert.False(host.IsEnabled);

        host.IsActive = true;

        Assert.Equal(1, host.Opacity);
        Assert.True(host.IsHitTestVisible);
        Assert.True(host.IsEnabled);
        Assert.Equal(bounds, probe.Bounds);
    }

    [Fact]
    public void ClipFilterChanges_OnlyNotifyAggregateVisibilityWhenItChanges()
    {
        var clip = new ClipCardViewModel(CreateMedia(), Path.GetTempPath());
        var notifications = 0;
        clip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClipCardViewModel.IsVisibleInLibrary)) notifications++;
        };

        clip.IsMatchedByGameFilter = false;
        clip.IsMatchedByClipTypeFilter = false;
        clip.IsMatchedByGameFilter = true;
        clip.IsMatchedByClipTypeFilter = true;

        Assert.Equal(2, notifications);
    }

    private static MediaFileInfo CreateMedia() => new(
        "clip.mp4",
        Path.Combine(Path.GetTempPath(), "clip.mp4"),
        DateTimeOffset.UtcNow,
        TimeSpan.FromSeconds(10),
        1,
        string.Empty,
        Array.Empty<MediaTrackInfo>(),
        1920,
        1080,
        60);

    private sealed class MeasureProbe : Control
    {
        public int MeasureCount { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            MeasureCount++;
            return new Size(100, 80);
        }
    }
}
