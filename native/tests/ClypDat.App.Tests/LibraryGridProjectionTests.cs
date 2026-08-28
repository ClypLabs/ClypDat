using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LibraryGridProjectionTests
{
    [Fact]
    public void ReconcileRows_ThreeToFourColumns_KeepsRowsAndNeverResets()
    {
        var clips = CreateClips(8);
        var rows = Rows(clips, 3);
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

    private static ObservableCollection<LibraryGridRow> Rows(IReadOnlyList<ClipCardViewModel> clips, int columns) =>
        new(Enumerable.Range(0, (clips.Count + columns - 1) / columns)
            .Select(index => new LibraryGridRow(clips.Skip(index * columns).Take(columns).ToArray(), index)));

    private static ClipCardViewModel[] CreateClips(int count) =>
        Enumerable.Range(0, count).Select(index => new ClipCardViewModel(
            new MediaFileInfo($"clip-{index}", $"C:\\test\\clip-{index}.mp4", DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(1), 1, string.Empty, Array.Empty<MediaTrackInfo>(), 1, 1, 60),
            "C:\\test")).ToArray();
}
