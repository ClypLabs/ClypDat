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
        QwertyFull => new(QwertyFull, "qwerty_full", 1989, 540, ["E", "A", "D", "L", "MouseLeft"]),
        Arrows => new(Arrows, "arrow_keys", 679, 434, ["Up", "MouseLeft"]),
        AzertyCompact => new(AzertyCompact, "azerty_truncated", 1124, 540, ["Z", "MouseLeft"]),
        _ => new(QwertyCompact, "qwerty_truncated", 1124, 540, ["W", "MouseLeft"])
    };

    public static bool IsKnown(string? layout) => layout is "None" or QwertyFull or QwertyCompact or Arrows or AzertyCompact;
}

public sealed record KeyboardOverlayDefinition(
    string DisplayName, string MedalLayoutId, int NativeWidth, int NativeHeight, IReadOnlyList<string> SamplePressed)
{
    public double AspectRatio => (double)NativeWidth / NativeHeight;
}
