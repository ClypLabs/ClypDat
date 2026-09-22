using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorSurfaceCoversTests
{
    private DateTime _now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private EditorSurfaceCovers NewCovers() => new(() => _now);

    [Fact]
    public void StaysCoveredUntilEveryCoverIsReleased()
    {
        var covers = NewCovers();
        var a = covers.Acquire("a");
        var b = covers.Acquire("b");

        b.Dispose();
        Assert.True(covers.IsCovered);
        a.Dispose();
        Assert.False(covers.IsCovered);
        Assert.Equal("none", covers.Describe());
    }

    [Fact]
    public void OrphanedCoverIsReclaimedOnlyAfterMinimumAge()
    {
        var covers = NewCovers();
        var windowGone = true;
        covers.Acquire("dialog", () => windowGone);

        Assert.Empty(covers.ReleaseOrphaned(TimeSpan.FromSeconds(5)));
        Assert.True(covers.IsCovered);

        _now += TimeSpan.FromSeconds(6);
        Assert.Equal(new[] { "dialog" }, covers.ReleaseOrphaned(TimeSpan.FromSeconds(5)));
        Assert.False(covers.IsCovered);
    }
}
