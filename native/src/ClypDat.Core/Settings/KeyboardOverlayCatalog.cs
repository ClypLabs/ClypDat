namespace ClypDat.Core.Settings;

/// <summary>Stable names and native canvases used by the keyboard/mouse overlay.</summary>
public static class KeyboardOverlayCatalog
{
    public const string QwertyFull = "QWERTY Full";
    public const string QwertyCompact = "QWERTY Compact";
    public const string Arrows = "Arrows";
    public const string AzertyCompact = "AZERTY Compact";

    public static KeyboardOverlayDefinition Get(string? layout) => layout switch
    {
        // Physical positions, not printed letters - the same thing recorded
        // input is matched against. AZERTY's "Z" cap sits at KeyW, and the
        // Arrows sample used to name "Up" for a cap the board draws as an
        // arrow glyph, so it could never match anything.
        QwertyFull => new(QwertyFull, "qwerty_full", 1989, 540, ["KeyE", "KeyA", "KeyD", "KeyL", "MouseLeft"]),
        Arrows => new(Arrows, "arrow_keys", 679, 434, ["ArrowUp", "MouseLeft"]),
        AzertyCompact => new(AzertyCompact, "azerty_truncated", 1124, 540, ["KeyW", "MouseLeft"]),
        _ => new(QwertyCompact, "qwerty_truncated", 1124, 540, ["KeyW", "MouseLeft"])
    };

    public const string Custom = "Custom Keys";

    /// <summary>
    /// The definition for a layout selection, resolving a custom reference
    /// against the user's sets. Prefer this over Get wherever the settings are in
    /// scope: Get cannot see the list, so a custom selection falls through its
    /// catch-all to QWERTY Compact and would be placed at the wrong aspect.
    /// </summary>
    public static KeyboardOverlayDefinition Resolve(string? layout, IEnumerable<CustomKeyboardLayout>? customLayouts)
    {
        var custom = CustomKeyboardLibrary.Find(customLayouts, layout);
        return custom is not null ? ForCustom(custom) : Get(layout);
    }

    /// <summary>
    /// A definition for a user-built set. The canvas is derived from the packed
    /// board rather than declared, because a board assembled at runtime has no
    /// canvas of its own and every placement site works from this aspect.
    /// </summary>
    public static KeyboardOverlayDefinition ForCustom(CustomKeyboardLayout layout)
    {
        var board = CustomKeyboardBoard.Pack(layout.Keys, layout.IncludeMouse);
        var aspect = CustomKeyboardBoard.AspectRatio(board);
        const int height = 540;
        return new(Custom, "custom", Math.Max(1, (int)Math.Round(height * aspect)), height,
            layout.Keys.Take(1).Append("MouseLeft").ToArray());
    }

    /// <summary>
    /// Whether a layout string is one this app can draw. Custom references are
    /// checked for shape only - this has no view of the settings list, so a
    /// reference to a set that was since deleted still passes here and is pruned
    /// where the list is in scope.
    /// </summary>
    public static bool IsKnown(string? layout) =>
        layout is "None" or QwertyFull or QwertyCompact or Arrows or AzertyCompact ||
        CustomKeyboardLibrary.IsCustomSelection(layout);
}

public sealed record KeyboardOverlayDefinition(
    string DisplayName, string MedalLayoutId, int NativeWidth, int NativeHeight, IReadOnlyList<string> SamplePressed)
{
    public double AspectRatio => (double)NativeWidth / NativeHeight;
}
