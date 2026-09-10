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

    public static bool IsKnown(string? layout) => layout is "None" or QwertyFull or QwertyCompact or Arrows or AzertyCompact;
}

public sealed record KeyboardOverlayDefinition(
    string DisplayName, string MedalLayoutId, int NativeWidth, int NativeHeight, IReadOnlyList<string> SamplePressed)
{
    public double AspectRatio => (double)NativeWidth / NativeHeight;
}
