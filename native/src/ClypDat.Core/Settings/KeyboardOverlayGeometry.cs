namespace ClypDat.Core.Settings;

/// <summary>Physical keyboard geometry shared by preview and recorder renderers.</summary>
public static class KeyboardOverlayGeometry
{
    public const double Gap = .14;
    public const double MouseGap = .45;
    public const double ClusterGap = .3;
    public const double MouseAspect = .53;
    public const double MouseOnlyHeight = 3;

    public sealed record Key(string Label, double Units = 1, string? Code = null);
    public sealed record Row(double Offset, IReadOnlyList<Key> Keys);
    public sealed record Board(IReadOnlyList<Row> Rows, IReadOnlyList<Row>? Cluster = null, bool IncludeMouse = true);

    public static Board FromCustom(CustomKeyboardBoardShape shape) => new(
        shape.Rows.Select(ToRow).ToArray(),
        shape.Cluster.Count == 0 ? null : shape.Cluster.Select(ToRow).ToArray(), shape.IncludeMouse);

    public static Board Describe(string layout) => layout switch
    {
        KeyboardOverlayCatalog.QwertyFull => new Board(
        [
            new(0, [new("`", 1, "Backquote"), .. Letters("1234567890", DigitRowCodes), new("-", 1, "Minus"), new("=", 1, "Equal"), new("Bksp", 2, "Backspace")]),
            new(0, [new("Tab", 1.5, "Tab"), .. Letters("QWERTYUIOP", TopRowCodes), new("[", 1, "BracketLeft"), new("]", 1, "BracketRight"), new("\\", 1.5, "Backslash")]),
            new(0, [new("Caps", 1.75, "CapsLock"), .. Letters("ASDFGHJKL", HomeRowCodes), new(";", 1, "Semicolon"), new("’", 1, "Quote"), new("Enter", 2.25, "Enter")]),
            new(0, [new("Shift", 2.25, "ShiftLeft"), .. Letters("ZXCVBNM", BottomRowCodes), new(",", 1, "Comma"), new(".", 1, "Period"), new("/", 1, "Slash"), new("Shift", 2.75, "ShiftRight")]),
            new(0, [new("Ctrl", 1.25, "ControlLeft"), new("Win", 1.25, "MetaLeft"), new("Alt", 1.25, "AltLeft"), new("Space", 7.5, "Space"), new("Alt", 1.25, "AltRight"), new("Fn", 1.25), new("Ctrl", 1.25, "ControlRight")])
        ], [new(1 + Gap, [new("↑", 1, "ArrowUp")]), new(0, [new("←", 1, "ArrowLeft"), new("↓", 1, "ArrowDown"), new("→", 1, "ArrowRight")])]),
        KeyboardOverlayCatalog.Arrows => new Board([new(1 + Gap, [new("↑", 1, "ArrowUp")]), new(0, [new("←", 1, "ArrowLeft"), new("↓", 1, "ArrowDown"), new("→", 1, "ArrowRight")])]),
        KeyboardOverlayCatalog.AzertyCompact => Truncated("AZERTYUIOP", "QSDFGHJKLM", "WXCVBN"),
        _ => Truncated("QWERTYUIOP", "ASDFGHJKL", "ZXCVBN")
    };

    private static Row ToRow(IReadOnlyList<CustomKeyCap> caps) => new(0, caps.Select(cap => new Key(cap.Label, cap.Units, cap.Code)).ToArray());
    private static Board Truncated(string top, string home, string bottom) => new([new(0, Letters(top, TopRowCodes)), new(.42, Letters(home, HomeRowCodes)), new(.72, Letters(bottom, BottomRowCodes)), new(0, [new("Ctrl", 1.4, "ControlLeft"), new("Space", 3.8, "Space")])]);
    private static readonly string[] TopRowCodes = ["KeyQ", "KeyW", "KeyE", "KeyR", "KeyT", "KeyY", "KeyU", "KeyI", "KeyO", "KeyP"];
    private static readonly string[] HomeRowCodes = ["KeyA", "KeyS", "KeyD", "KeyF", "KeyG", "KeyH", "KeyJ", "KeyK", "KeyL", "Semicolon"];
    private static readonly string[] BottomRowCodes = ["KeyZ", "KeyX", "KeyC", "KeyV", "KeyB", "KeyN", "KeyM"];
    private static readonly string[] DigitRowCodes = ["Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "Digit8", "Digit9", "Digit0"];
    private static IReadOnlyList<Key> Letters(string letters, IReadOnlyList<string> codes) => letters.Select((letter, index) => new Key(letter.ToString(), 1, index < codes.Count ? codes[index] : null)).ToArray();
}
