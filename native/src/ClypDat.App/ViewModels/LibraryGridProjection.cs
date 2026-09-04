using System.Collections.ObjectModel;

namespace ClypDat.App.ViewModels;

public sealed class LibraryGridRow : ViewModelBase
{
    private IReadOnlyList<ClipCardViewModel> _projectedClips;

    public LibraryGridRow(IReadOnlyList<ClipCardViewModel> clips, int index)
    {
        _projectedClips = clips;
        Clips = new ObservableCollection<ClipCardViewModel>(clips);
        Index = index;
    }

    public ObservableCollection<ClipCardViewModel> Clips { get; }
    internal IReadOnlyList<ClipCardViewModel> ProjectedClips => _projectedClips;
    internal bool IsRealized { get; private set; }
    public int Index { get; private set; }

    internal void UpdateProjection(IReadOnlyList<ClipCardViewModel> clips, int index)
    {
        _projectedClips = clips;
        if (IsRealized) ReconcileClips(clips);
        if (Index == index) return;
        Index = index;
        OnPropertyChanged(nameof(Index));
    }

    internal bool SetRealized(bool realized)
    {
        if (IsRealized == realized) return false;
        IsRealized = realized;
        if (realized) ReconcileClips(_projectedClips);
        return true;
    }

    private void ReconcileClips(IReadOnlyList<ClipCardViewModel> clips)
    {
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
internal readonly record struct LibraryViewportAnchor(string? Path, int VisibleOrdinal, double IntraRowFraction);

internal sealed record LibraryGridProjectionResult(
    IReadOnlyList<LibraryGridRow> Rows,
    IReadOnlyList<LibraryGridDateMarker> DateMarkers,
    int VisibleCount,
    IReadOnlyDictionary<string, int> RowByPath,
    IReadOnlyDictionary<string, int> OrdinalByPath,
    IReadOnlyList<ClipCardViewModel> VisibleClips)
{
    internal LibraryViewportAnchor CaptureAnchor(double offsetY, double rowPitch, int columns)
    {
        if (VisibleCount == 0) return new(null, 0, 0);
        rowPitch = Math.Max(1, rowPitch);
        columns = Math.Max(1, columns);
        var rowPosition = Math.Max(0, offsetY) / rowPitch;
        var row = Math.Min((int)Math.Floor(rowPosition), Math.Max(0, Rows.Count - 1));
        var ordinal = Math.Min(row * columns, VisibleCount - 1);
        return new(VisibleClips[ordinal].Path, ordinal, Math.Clamp(rowPosition - row, 0, 1));
    }

    internal double ResolveAnchor(LibraryViewportAnchor anchor, double rowPitch, int columns, double maximumOffset)
    {
        if (VisibleCount == 0) return 0;
        columns = Math.Max(1, columns);
        var ordinal = anchor.Path is not null && OrdinalByPath.TryGetValue(anchor.Path, out var mapped)
            ? mapped
            : Math.Clamp(anchor.VisibleOrdinal, 0, VisibleCount - 1);
        var row = ordinal / columns;
        return Math.Clamp((row + Math.Clamp(anchor.IntraRowFraction, 0, 1)) * Math.Max(1, rowPitch), 0, Math.Max(0, maximumOffset));
    }
}

internal static class LibraryGridProjection
{
    internal static void ReconcileRows(IList<LibraryGridRow> currentRows, IReadOnlyList<LibraryGridRow> projectedRows)
    {
        var sharedCount = Math.Min(currentRows.Count, projectedRows.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            var projected = projectedRows[index];
            currentRows[index].UpdateProjection(projected.ProjectedClips, projected.Index);
        }
        for (var index = currentRows.Count - 1; index >= projectedRows.Count; index--) currentRows.RemoveAt(index);
        for (var index = currentRows.Count; index < projectedRows.Count; index++) currentRows.Add(projectedRows[index]);
    }

    public static LibraryGridProjectionResult Build(IReadOnlyList<ClipCardViewModel> clips, int columns)
    {
        columns = Math.Max(1, columns);
        var visible = new List<ClipCardViewModel>(clips.Count);
        var ordinalByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rowByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dateCounts = new Dictionary<DateTime, int>();
        var orderedDates = new List<(DateTime Date, int FirstOrdinal)>();
        foreach (var clip in clips)
        {
            if (!clip.IsVisibleInLibrary) continue;
            var ordinal = visible.Count;
            visible.Add(clip);
            ordinalByPath[clip.Path] = ordinal;
            rowByPath[clip.Path] = ordinal / columns;
            var date = clip.CreatedAt.ToLocalTime().Date;
            if (dateCounts.TryGetValue(date, out var count)) dateCounts[date] = count + 1;
            else { dateCounts[date] = 1; orderedDates.Add((date, ordinal)); }
        }

        var rows = new List<LibraryGridRow>((visible.Count + columns - 1) / columns);
        for (var offset = 0; offset < visible.Count; offset += columns)
            rows.Add(new LibraryGridRow(visible.GetRange(offset, Math.Min(columns, visible.Count - offset)), rows.Count));

        var nowYear = DateTime.Now.Year;
        var markers = new List<LibraryGridDateMarker>(orderedDates.Count);
        foreach (var (date, firstOrdinal) in orderedDates)
        {
            var format = date.Year == nowYear ? "MMM d" : "MMM d, yyyy";
            markers.Add(new(date.ToString(format).ToUpperInvariant(), firstOrdinal / columns, dateCounts[date]));
        }
        return new(rows, markers, visible.Count, rowByPath, ordinalByPath, visible);
    }
}
