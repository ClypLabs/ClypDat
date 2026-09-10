namespace ClypDat.Core.Settings;

/// <summary>
/// A user-built peripheral overlay: the physical key positions they chose, drawn
/// packed together rather than scattered across a full-size board.
///
/// Keys are stored as positions (KeyQ, ShiftLeft, Space) rather than letters, the
/// same vocabulary recorded input uses, so a set stays correct whatever keyboard
/// layout the machine is set to.
/// </summary>
public sealed class CustomKeyboardLayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom keys";
    public List<string> Keys { get; set; } = new();
    public bool IncludeMouse { get; set; } = true;
}

public static class CustomKeyboardLibrary
{
    public const string Prefix = "custom:";
    public const int MaximumKeys = 60;

    /// <summary>
    /// Whether a layout string references a custom set. Shape only: this lives in
    /// Core with no view of the settings list, so a reference to a set that was
    /// since deleted still looks well-formed here and is pruned where the list is
    /// actually in scope.
    /// </summary>
    public static bool IsCustomSelection(string? selection) =>
        selection?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true &&
        Guid.TryParse(selection[Prefix.Length..], out _);

    public static string Selection(CustomKeyboardLayout layout) => Prefix + layout.Id;

    public static string? IdOf(string? selection) =>
        IsCustomSelection(selection) ? selection![Prefix.Length..] : null;

    public static CustomKeyboardLayout? Find(IEnumerable<CustomKeyboardLayout>? layouts, string? selection)
    {
        var id = IdOf(selection);
        if (id is null || layouts is null) return null;
        return layouts.FirstOrDefault(layout => string.Equals(layout.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryNormalizeName(string? value, IEnumerable<CustomKeyboardLayout> existing,
        string? exceptId, out string name, out string? error)
    {
        name = value?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 64) { error = "Layout name must be 1–64 characters."; return false; }
        var candidate = name;
        if (existing.Any(layout => layout is not null && !string.Equals(layout.Id, exceptId, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(layout.Name?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
        { error = "Layout name already exists."; return false; }
        error = null;
        return true;
    }

    public static string UniqueName(string name, IEnumerable<CustomKeyboardLayout> existing)
    {
        name = name.Trim();
        if (!existing.Any(layout => string.Equals(layout.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!existing.Any(layout => string.Equals(layout.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    /// <summary>Drops unknown positions, duplicates and anything past the cap, keeping
    /// the caller's order irrelevant - packing decides the order.</summary>
    public static List<string> Sanitize(IEnumerable<string>? keys) => (keys ?? [])
        .Where(key => !string.IsNullOrWhiteSpace(key) && CustomKeyboardBoard.IsKnownPosition(key))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaximumKeys)
        .ToList();
}
