namespace ClypDat.App.ViewModels;

public sealed class LibraryGridRow : ViewModelBase
{
    public LibraryGridRow(IReadOnlyList<ClipCardViewModel> clips, int index)
    {
        Clips = clips;
        Index = index;
    }

    public IReadOnlyList<ClipCardViewModel> Clips { get; private set; }
    public int Index { get; private set; }

    internal void Update(IReadOnlyList<ClipCardViewModel> clips, int index)
    {
        if (!Clips.SequenceEqual(clips))
        {
            Clips = clips;
            OnPropertyChanged(nameof(Clips));
        }

        if (Index == index) return;
        Index = index;
        OnPropertyChanged(nameof(Index));
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
