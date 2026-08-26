namespace ClypDat.App.ViewModels;

public sealed record LibraryGridRow(IReadOnlyList<ClipCardViewModel> Clips, int Index);

internal readonly record struct LibraryGridDateMarker(string Text, int RowIndex, int Count);

internal sealed record LibraryGridProjectionResult(
    IReadOnlyList<LibraryGridRow> Rows,
    IReadOnlyList<LibraryGridDateMarker> DateMarkers,
    int VisibleCount,
    IReadOnlyDictionary<string, int> RowByPath);

internal static class LibraryGridProjection
{
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
