using Avalonia.Controls;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Services;

// ContainerClearing runs after Avalonia has cleared its content and DataContext.
// Remember the prepared row independently so recycling can release its cards.
internal sealed class LibraryRowRealization
{
    private readonly Dictionary<Control, LibraryGridRow> _containers = [];
    private readonly Dictionary<LibraryGridRow, int> _rows = [];

    internal IReadOnlyCollection<LibraryGridRow> Rows => _rows.Keys;

    internal void Prepared(Control container, LibraryGridRow row)
    {
        if (_containers.TryGetValue(container, out var previous) && ReferenceEquals(previous, row)) return;
        Cleared(container);
        _containers.Add(container, row);
        _rows[row] = _rows.GetValueOrDefault(row) + 1;
        row.SetRealized(true);
    }

    internal bool Cleared(Control container)
    {
        if (!_containers.Remove(container, out var row)) return false;
        if (_rows[row] > 1) _rows[row]--;
        else
        {
            _rows.Remove(row);
            row.SetRealized(false);
        }
        return true;
    }
}
