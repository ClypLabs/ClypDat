using Avalonia;
using Avalonia.Media;

namespace ClypDat.App.Services;

public sealed record ThemeOption(
    string Id,
    string Label,
    IBrush AppBackgroundBrush,
    IBrush SurfaceBrush,
    IBrush AccentBrush);

/// <summary>Owns ClypDat's mutable dark-theme resource palette.</summary>
internal static class AppThemeService
{
    private sealed record Palette(
        Color App, Color Panel, Color Surface, Color Raised, Color Hover,
        Color Edge, Color EdgeStrong, Color Divider, Color Accent);

    private static readonly Palette System = new(
        Color.Parse("#0D1116"), Color.Parse("#101820"), Color.Parse("#141D24"), Color.Parse("#1A242E"),
        Color.Parse("#22303D"), Color.Parse("#232F3A"), Color.Parse("#2C3B48"), Color.Parse("#1B242D"),
        Color.Parse("#5864E8"));

    private static readonly IReadOnlyDictionary<string, Palette> Palettes = new Dictionary<string, Palette>(StringComparer.OrdinalIgnoreCase)
    {
        ["ClypDat Blue"] = new(Color.Parse("#0D0F19"), Color.Parse("#121524"), Color.Parse("#181B30"), Color.Parse("#202641"), Color.Parse("#2C3457"), Color.Parse("#2B3350"), Color.Parse("#3A466B"), Color.Parse("#22283F"), Color.Parse("#5864E8")),
        ["Violet"] = new(Color.Parse("#110E18"), Color.Parse("#181221"), Color.Parse("#20172A"), Color.Parse("#2A1E37"), Color.Parse("#38294A"), Color.Parse("#382A48"), Color.Parse("#4A3860"), Color.Parse("#2A2037"), Color.Parse("#8B5CF6")),
        ["Emerald"] = new(Color.Parse("#0B1412"), Color.Parse("#0E1B17"), Color.Parse("#12231D"), Color.Parse("#182E27"), Color.Parse("#213E34"), Color.Parse("#223C34"), Color.Parse("#2E5146"), Color.Parse("#1A3029"), Color.Parse("#10B981")),
        ["Rose"] = new(Color.Parse("#170D12"), Color.Parse("#211118"), Color.Parse("#2B1620"), Color.Parse("#381D29"), Color.Parse("#4A2836"), Color.Parse("#482735"), Color.Parse("#603548"), Color.Parse("#37202A"), Color.Parse("#F43F5E")),
        ["Amber"] = new(Color.Parse("#17120A"), Color.Parse("#21190D"), Color.Parse("#2B2112"), Color.Parse("#382B18"), Color.Parse("#4A3A21"), Color.Parse("#493821"), Color.Parse("#604A2D"), Color.Parse("#382B1B"), Color.Parse("#F59E0B"))
    };

    public static IReadOnlyList<ThemeOption> Options { get; } = new[]
    {
        Option("System", "System", System),
        Option("ClypDat Blue", "ClypDat Blue", Palettes["ClypDat Blue"]),
        Option("Violet", "Violet", Palettes["Violet"]),
        Option("Emerald", "Emerald", Palettes["Emerald"]),
        Option("Rose", "Rose", Palettes["Rose"]),
        Option("Amber", "Amber", Palettes["Amber"])
    };

    public static string Normalize(string? preset) =>
        string.Equals(preset, "System", StringComparison.OrdinalIgnoreCase) || Palettes.ContainsKey(preset ?? string.Empty)
            ? preset ?? "System"
            : "System";

    public static bool UsesSystemAccent(string preset) => string.Equals(Normalize(preset), "System", StringComparison.OrdinalIgnoreCase);

    public static void Apply(Application application, string preset, Color systemAccent)
    {
        preset = Normalize(preset);
        var palette = UsesSystemAccent(preset) ? System with { Accent = systemAccent } : Palettes[preset];
        Set(application, "AppBgBrush", palette.App);
        Set(application, "PanelBgBrush", palette.Panel);
        Set(application, "SurfaceBrush", palette.Surface);
        Set(application, "SurfaceRaisedBrush", palette.Raised);
        Set(application, "SurfaceHoverBrush", palette.Hover);
        Set(application, "EdgeBrush", palette.Edge);
        Set(application, "EdgeStrongBrush", palette.EdgeStrong);
        Set(application, "DividerBrush", palette.Divider);
        Set(application, "AccentBrush", palette.Accent);
        Set(application, "AccentBrushHover", BlendWithWhite(palette.Accent, 0.18));
        Set(application, "AccentSelectedBrush", Blend(palette.Accent, palette.App, 0.78));
        Set(application, "AccentSelectedHoverBrush", Blend(palette.Accent, palette.App, 0.84));
        Set(application, "AccentSelectedIconBrush", BlendWithWhite(palette.Accent, 0.55));
        Set(application, "AccentGameHoverBrush", Blend(palette.Accent, palette.App, 0.84));
        Set(application, "AccentFolderBrush", Blend(palette.Accent, palette.App, 0.55));
        Set(application, "AccentHoverBrush", Blend(palette.Accent, palette.App, 0.84));
    }

    private static ThemeOption Option(string id, string label, Palette palette) => new(
        id, label, new SolidColorBrush(palette.App), new SolidColorBrush(palette.Surface), new SolidColorBrush(palette.Accent));

    private static void Set(Application application, string key, Color color)
    {
        if (application.Resources[key] is SolidColorBrush brush) brush.Color = color;
    }

    private static Color BlendWithWhite(Color color, double amount) => Blend(color, Colors.White, amount);
    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (byte)(from.A + (to.A - from.A) * amount),
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));
}
