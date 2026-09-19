using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LibraryGridProjectionTests
{
    [Fact]
    public void CalculateLayout_PreservesFractionalSixteenByNineImageHeight()
    {
        var layout = LibraryCardLayoutCalculator.Calculate(1000, scaleWithWindow: false);

        Assert.Equal(309, layout.Width);
        Assert.Equal(309d * 9d / 16d, layout.ImageHeight);
    }

    [Fact]
    public void ReconcileRows_ThreeToFourColumns_KeepsRowsAndNeverResets()
    {
        var clips = CreateClips(8);
        var rows = Rows(clips, 3);
        Realize(rows);
        var originalRows = rows.ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, change) => changes.Add(change.Action);

        LibraryGridProjection.ReconcileRows(rows, Rows(clips, 4));

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Same(originalRows[0], rows[0]);
        Assert.Same(originalRows[1], rows[1]);
        Assert.Equal(clips, rows.SelectMany(row => row.Clips));
        Assert.All(rows.Take(2), row => Assert.Equal(4, row.Clips.Count));
    }

    [Fact]
    public void ReconcileRows_UnchangedProjection_DoesNotNotify()
    {
        var clips = CreateClips(6);
        var rows = Rows(clips, 3);
        Realize(rows);
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, change) => changes.Add(change.Action);

        LibraryGridProjection.ReconcileRows(rows, Rows(clips, 3));

        Assert.Empty(changes);
    }

    [Fact]
    public void ReconcileRows_FourToThreeColumns_KeepsRowsAndNeverResets()
    {
        var clips = CreateClips(8);
        var rows = Rows(clips, 4);
        Realize(rows);
        var originalRows = rows.ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, change) => changes.Add(change.Action);

        LibraryGridProjection.ReconcileRows(rows, Rows(clips, 3));

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Same(originalRows[0], rows[0]);
        Assert.Same(originalRows[1], rows[1]);
        Assert.Equal(clips, rows.SelectMany(row => row.Clips));
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
    }

    [Fact]
    public void ReconcileRows_ColumnChange_DoesNotNotifyUnrealizedRows()
    {
        var clips = CreateClips(20);
        var rows = Rows(clips, 3);
        rows[1].SetRealized(true);
        var unrealizedEvents = 0;
        var realizedEvents = 0;
        rows[0].Clips.CollectionChanged += (_, _) => unrealizedEvents++;
        rows[1].Clips.CollectionChanged += (_, _) => realizedEvents++;

        LibraryGridProjection.ReconcileRows(rows, Rows(clips, 4));

        Assert.Equal(0, unrealizedEvents);
        Assert.InRange(realizedEvents, 1, 7);
        Assert.Equal(clips.Take(3), rows[0].Clips);
        rows[0].SetRealized(true);
        Assert.Equal(clips.Take(4), rows[0].Clips);
    }

    [Theory]
    [InlineData(410)]
    [InlineData(5000)]
    public void Build_LargeProjection_ProducesRowsDatesAndOrdinalMaps(int count)
    {
        var clips = CreateClips(count);
        var projection = LibraryGridProjection.Build(clips, 4);

        Assert.Equal(count, projection.VisibleCount);
        Assert.Equal((count + 3) / 4, projection.Rows.Count);
        Assert.Equal(count, projection.RowByPath.Count);
        Assert.Equal(count, projection.OrdinalByPath.Count);
        Assert.Equal(count, projection.VisibleClips.Count);
        Assert.Equal(count - 1, projection.OrdinalByPath[clips[^1].Path]);
        Assert.Equal((count - 1) / 4, projection.RowByPath[clips[^1].Path]);
        Assert.Single(projection.DateMarkers);
        Assert.Equal(count, projection.DateMarkers[0].Count);
    }

    [Fact]
    public void Anchor_ColumnReflow_PreservesPathAndFraction()
    {
        var clips = CreateClips(20);
        var anchor = LibraryGridProjection.Build(clips, 3).CaptureAnchor(2.4 * 200, 200, 3);
        var offset = LibraryGridProjection.Build(clips, 4).ResolveAnchor(anchor, 200, 4, 5000);

        Assert.Equal(clips[6].Path, anchor.Path);
        Assert.Equal(280, offset, 6);
    }

    [Fact]
    public void Anchor_MissingPath_UsesClampedOrdinalFallback()
    {
        var clips = CreateClips(5);
        var anchor = new LibraryViewportAnchor("C:\\missing.mp4", 99, 0.5);
        var offset = LibraryGridProjection.Build(clips, 3).ResolveAnchor(anchor, 100, 3, 125);

        Assert.Equal(125, offset);
        Assert.Equal(0, LibraryGridProjection.Build([], 3).ResolveAnchor(anchor, 100, 3, 125));
    }

    [Fact]
    public void Build_FilteredDates_MapsOnlyVisibleClipsAndCountsEachDate()
    {
        var clips = CreateClips(6);
        clips[1].IsMatchedBySearch = false;
        clips[4].IsMatchedBySearch = false;
        foreach (var clip in clips.Take(3)) clip.UpdateMedia(clip.Media with { CreatedAt = DateTimeOffset.Now.Date.AddDays(-1) });
        foreach (var clip in clips.Skip(3)) clip.UpdateMedia(clip.Media with { CreatedAt = DateTimeOffset.Now.Date.AddDays(-2) });

        var projection = LibraryGridProjection.Build(clips, 3);

        Assert.Equal(4, projection.VisibleCount);
        Assert.False(projection.OrdinalByPath.ContainsKey(clips[1].Path));
        Assert.False(projection.RowByPath.ContainsKey(clips[4].Path));
        Assert.Equal([2, 2], projection.DateMarkers.Select(marker => marker.Count));
        Assert.Equal([0, 0], projection.DateMarkers.Select(marker => marker.RowIndex));
    }

    private static ObservableCollection<LibraryGridRow> Rows(IReadOnlyList<ClipCardViewModel> clips, int columns) =>
        new(Enumerable.Range(0, (clips.Count + columns - 1) / columns)
            .Select(index => new LibraryGridRow(clips.Skip(index * columns).Take(columns).ToArray(), index)));

    private static void Realize(IEnumerable<LibraryGridRow> rows)
    {
        foreach (var row in rows) row.SetRealized(true);
    }

    private static ClipCardViewModel[] CreateClips(int count) =>
        Enumerable.Range(0, count).Select(index => new ClipCardViewModel(
            new MediaFileInfo($"clip-{index}", $"C:\\test\\clip-{index}.mp4", DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(1), 1, string.Empty, Array.Empty<MediaTrackInfo>(), 1, 1, 60),
            "C:\\test")).ToArray();
}
