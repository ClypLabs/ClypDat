using System.Collections.ObjectModel;

namespace ClypDat.App.ViewModels;

public sealed class LibraryGridRow : ViewModelBase
{
    public LibraryGridRow(IReadOnlyList<ClipCardViewModel> clips, int index)
    {
        Clips = new ObservableCollection<ClipCardViewModel>(clips);
        Index = index;
    }

    // Keep this source stable. Replacing it makes Avalonia remove every card
    // container in the nested ItemsControl, which is visible as a blank grid
    // whenever a resize changes the number of columns.
    public ObservableCollection<ClipCardViewModel> Clips { get; }
    public int Index { get; private set; }

    internal void Update(IReadOnlyList<ClipCardViewModel> clips, int index)
    {
        ReconcileClips(clips);

        if (Index == index) return;
        Index = index;
        OnPropertyChanged(nameof(Index));
    }

    private void ReconcileClips(IReadOnlyList<ClipCardViewModel> clips)
    {
        // Insert or move desired cards before removing stale ones. In
        // particular, a row changing from 3 to 4 cards can never become empty
        // between collection notifications, so Avalonia always has a live
        // card tree to arrange while the resize is in progress.
        for (var index = 0; index < clips.Count; index++)
        {
            if (index < Clips.Count && ReferenceEquals(Clips[index], clips[index])) continue;

            var existingIndex = -1;
            for (var candidate = index + 1; candidate < Clips.Count; candidate++)
            {
                if (!ReferenceEquals(Clips[candidate], clips[index])) continue;
                existingIndex = candidate;
                break;
            }

            if (existingIndex >= 0) Clips.Move(existingIndex, index);
            else Clips.Insert(index, clips[index]);
        }

        while (Clips.Count > clips.Count) Clips.RemoveAt(Clips.Count - 1);
    }
}

internal readonly record struct LibraryGridDateMarker(string Text, int RowIndex, int Count);

internal sealed record LibraryGridProjectionResult(
    IReadOnlyList<LibraryGridRow> Rows,
    IReadOnlyList<LibraryGridDateMarker> DateMarkers,
    int VisibleCount,
    IReadOnlyDictionary<string, int> RowByPath);

internal static class LibraryGridProjection
{
    internal static void ReconcileRows(
        IList<LibraryGridRow> currentRows,
        IReadOnlyList<LibraryGridRow> projectedRows)
    {
        var sharedCount = Math.Min(currentRows.Count, projectedRows.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            var projected = projectedRows[index];
            currentRows[index].Update(projected.Clips, projected.Index);
        }

        for (var index = currentRows.Count - 1; index >= projectedRows.Count; index--)
            currentRows.RemoveAt(index);

        for (var index = currentRows.Count; index < projectedRows.Count; index++)
            currentRows.Add(projectedRows[index]);
    }

    public static LibraryGridProjectionResult Build(IReadOnlyList<ClipCardViewModel> clips, int columns)
    {
        columns = Math.Max(1, columns);
        var visible = clips.Where(clip => clip.IsVisibleInLibrary).ToArray();
        var rows = new List<LibraryGridRow>((visible.Length + columns - 1) / columns);
        var rowByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset < visible.Length; offset += columns)
        {
            var row = visible.Skip(offset).Take(columns).ToArray();
            var rowIndex = rows.Count;
            rows.Add(new LibraryGridRow(row, rowIndex));
            foreach (var clip in row) rowByPath[clip.Path] = rowIndex;
        }

        var markers = new List<LibraryGridDateMarker>();
        var seenDates = new HashSet<DateTime>();
        for (var index = 0; index < visible.Length; index++)
        {
            var local = visible[index].CreatedAt.ToLocalTime();
            if (!seenDates.Add(local.Date)) continue;
            var rowIndex = index / columns;
            var count = visible.Count(clip => clip.CreatedAt.ToLocalTime().Date == local.Date);
            var format = local.Year == DateTime.Now.Year ? "MMM d" : "MMM d, yyyy";
            markers.Add(new LibraryGridDateMarker(local.ToString(format).ToUpperInvariant(), rowIndex, count));
        }

        return new LibraryGridProjectionResult(rows, markers, visible.Length, rowByPath);
    }
}
