namespace ClypDat.Core.Settings;

/// <summary>One drawn cap: where it came from, what it says, how wide it is.</summary>
public sealed record CustomKeyCap(string Code, string Label, double Units);

/// <summary>
/// A packed board. Rows are already in top-to-bottom order and each row is in
/// left-to-right order; the arrow keys, if any, come back as a cluster so they
/// draw as the inverted-T they are on a real keyboard rather than as two more
/// letter rows.
/// </summary>
public sealed record CustomKeyboardBoardShape(
    IReadOnlyList<IReadOnlyList<CustomKeyCap>> Rows,
    IReadOnlyList<IReadOnlyList<CustomKeyCap>> Cluster,
    bool IncludeMouse);

/// <summary>
/// Turns a set of physical key positions into a board that reads like a keyboard
/// while taking only the space the chosen keys need.
///
/// Packed, not positional: a set of W A S D draws W directly above A S D rather
/// than at W's true indent with holes either side. A positional board would be
/// mostly empty space, and an overlay is competing with the game for screen.
/// </summary>
public static class CustomKeyboardBoard
{
    // Left-to-right order within each row is the real keyboard's. A row that ends
    // up empty is dropped, so these are templates rather than a fixed grid.
    private static readonly string[][] RowTemplates =
    [
        ["Escape", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "ScrollLock"],
        ["Backquote", "Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "Digit8", "Digit9", "Digit0", "Minus", "Equal", "Backspace"],
        ["Tab", "KeyQ", "KeyW", "KeyE", "KeyR", "KeyT", "KeyY", "KeyU", "KeyI", "KeyO", "KeyP", "BracketLeft", "BracketRight", "Backslash"],
        ["CapsLock", "KeyA", "KeyS", "KeyD", "KeyF", "KeyG", "KeyH", "KeyJ", "KeyK", "KeyL", "Semicolon", "Quote", "Enter"],
        ["ShiftLeft", "KeyZ", "KeyX", "KeyC", "KeyV", "KeyB", "KeyN", "KeyM", "Comma", "Period", "Slash", "ShiftRight"],
        ["ControlLeft", "MetaLeft", "AltLeft", "Space", "AltRight", "MetaRight", "ContextMenu", "ControlRight"],
        ["Insert", "Home", "PageUp"],
        ["Delete", "End", "PageDown"],
        ["NumLock", "NumpadDivide", "NumpadMultiply", "NumpadSubtract"],
        ["Numpad7", "Numpad8", "Numpad9", "NumpadAdd"],
        ["Numpad4", "Numpad5", "Numpad6"],
        ["Numpad1", "Numpad2", "Numpad3", "NumpadEnter"],
        ["Numpad0", "NumpadDecimal"],
    ];

    private static readonly string[][] ArrowTemplates = [["ArrowUp"], ["ArrowLeft", "ArrowDown", "ArrowRight"]];

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Escape"] = "Esc", ["Backquote"] = "`", ["Minus"] = "-", ["Equal"] = "=", ["Backspace"] = "Bksp",
        ["Tab"] = "Tab", ["BracketLeft"] = "[", ["BracketRight"] = "]", ["Backslash"] = "\\",
        ["CapsLock"] = "Caps", ["Semicolon"] = ";", ["Quote"] = "’", ["Enter"] = "Enter",
        ["ShiftLeft"] = "Shift", ["ShiftRight"] = "Shift", ["Comma"] = ",", ["Period"] = ".", ["Slash"] = "/",
        ["ControlLeft"] = "Ctrl", ["ControlRight"] = "Ctrl", ["MetaLeft"] = "Win", ["MetaRight"] = "Win",
        ["AltLeft"] = "Alt", ["AltRight"] = "Alt", ["ContextMenu"] = "Menu", ["Space"] = "Space",
        ["ArrowUp"] = "↑", ["ArrowDown"] = "↓", ["ArrowLeft"] = "←", ["ArrowRight"] = "→",
        ["Insert"] = "Ins", ["Home"] = "Home", ["PageUp"] = "PgUp", ["Delete"] = "Del", ["End"] = "End", ["PageDown"] = "PgDn",
        ["NumLock"] = "Num", ["NumpadDivide"] = "/", ["NumpadMultiply"] = "*", ["NumpadSubtract"] = "-",
        ["NumpadAdd"] = "+", ["NumpadEnter"] = "Enter", ["NumpadDecimal"] = ".",
    };

    // Kept modest deliberately. A 6u space bar is right on a full board and absurd
    // beside three letters, which is the case a custom set is usually built for.
    private static readonly Dictionary<string, double> Widths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 4, ["ShiftLeft"] = 2, ["ShiftRight"] = 2, ["Enter"] = 2, ["Backspace"] = 2,
        ["Tab"] = 1.5, ["CapsLock"] = 1.75, ["ControlLeft"] = 1.25, ["ControlRight"] = 1.25,
        ["AltLeft"] = 1.25, ["AltRight"] = 1.25, ["MetaLeft"] = 1.25, ["MetaRight"] = 1.25,
        ["ContextMenu"] = 1.25, ["NumpadEnter"] = 1.25, ["NumpadAdd"] = 1.25, ["Numpad0"] = 2,
    };

    private static readonly HashSet<string> Positions = new(
        RowTemplates.SelectMany(row => row).Concat(ArrowTemplates.SelectMany(row => row)),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownPosition(string? code) => code is not null && Positions.Contains(code);

    /// <summary>The label a position draws. Letters and digits carry their own.</summary>
    public static string LabelFor(string code) =>
        Labels.TryGetValue(code, out var label) ? label
        : code.StartsWith("Key", StringComparison.OrdinalIgnoreCase) ? code[3..].ToUpperInvariant()
        : code.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) ? code[5..]
        : code.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) ? code[6..]
        : code;

    public static double WidthFor(string code) => Widths.TryGetValue(code, out var units) ? units : 1;

    public static CustomKeyboardBoardShape Pack(IEnumerable<string>? codes, bool includeMouse)
    {
        var chosen = new HashSet<string>(codes ?? [], StringComparer.OrdinalIgnoreCase);
        var rows = RowTemplates
            .Select(template => template.Where(chosen.Contains).Select(Cap).ToArray())
            .Where(row => row.Length > 0)
            .Cast<IReadOnlyList<CustomKeyCap>>()
            .ToList();
        var cluster = ArrowTemplates
            .Select(template => template.Where(chosen.Contains).Select(Cap).ToArray())
            .Where(row => row.Length > 0)
            .Cast<IReadOnlyList<CustomKeyCap>>()
            .ToList();

        // The renderer cannot measure a board with no rows, and an overlay with
        // nothing on it is not worth drawing, so the arrows stand in as the board
        // when they are all that was chosen.
        if (rows.Count == 0 && cluster.Count > 0) { rows = cluster; cluster = []; }
        return new(rows, cluster, includeMouse);
    }

    private static CustomKeyCap Cap(string code) => new(code, LabelFor(code), WidthFor(code));

    /// <summary>
    /// The board's own aspect, in key units, including the mouse and the arrow
    /// cluster if present. The fixed layouts declare a canvas in the catalog that
    /// their board was tuned to fit; a board built at runtime has no such canvas,
    /// so its proportions have to come from its own shape or every placement
    /// calculation would size it against someone else's.
    /// </summary>
    public static double AspectRatio(CustomKeyboardBoardShape board)
    {
        var width = RowUnits(board.Rows);
        // Mirrors the renderer's floor for a mouse-only set.
        var height = Math.Max(board.Rows.Count == 0 ? 0 : board.Rows.Count + Gap * (board.Rows.Count - 1),
            board.IncludeMouse ? MouseOnlyHeight : 0);
        height = Math.Max(height, board.Cluster.Count == 0 ? 0 : board.Cluster.Count + Gap * (board.Cluster.Count - 1));
        if (board.Cluster.Count > 0) width += ClusterGap + RowUnits(board.Cluster);
        if (board.IncludeMouse) width += MouseGap + MouseAspect * height;
        return width <= 0 || height <= 0 ? 1 : width / height;
    }

    // Mirrors the renderer's own spacing so the aspect it is placed at and the
    // aspect it draws at agree.
    private const double Gap = .14, MouseGap = .45, ClusterGap = .3, MouseAspect = .53, MouseOnlyHeight = 3;

    private static double RowUnits(IReadOnlyList<IReadOnlyList<CustomKeyCap>> rows) => rows.Select(row =>
        row.Sum(cap => cap.Units + (cap.Units - 1) * Gap) + Gap * (row.Count - 1)).DefaultIfEmpty(0).Max();
}
