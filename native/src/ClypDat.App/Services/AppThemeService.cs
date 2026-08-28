using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
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
    // ClypDat's own surface family, as authored: a neutral blue-grey ramp. Every
    // themed colour in the app - the eight named tokens in Tokens.axaml and the
    // ~100 shades in SurfaceRamp.axaml alike - is this family run through one
    // hue/saturation/lightness transform. Nothing is hand-picked per theme except
    // the accent, so a shade that has no named token still lands in the right
    // place instead of being flattened onto the nearest one.
    //
    // The transforms below were fitted against the hand-picked palettes the preset
    // themes originally shipped with; every channel reproduces to within 4/255, so
    // switching to a derived palette is not a visible change.
    private sealed record ThemeTransform(
        double Hue,
        double SaturationScale, double SaturationBias,
        double LightnessScale, double LightnessBias)
    {
        public static readonly ThemeTransform Identity = new(-1, 1, 0, 1, 0);
        public bool IsIdentity => Hue < 0;
    }

    // Lightness is scaled fully across the range the named tokens occupy (up to
    // ~0.23) and eased back to the source value by 0.40. Without that taper the
    // >1 slope would drag the brightest ramp entries - hairline borders around
    // 0.38 - into mid-greys, which reads as a different design rather than a tint.
    private const double LightnessFullBelow = 0.23;
    private const double LightnessIdentityAbove = 0.40;

    private static readonly IReadOnlyDictionary<string, ThemeTransform> Transforms =
        new Dictionary<string, ThemeTransform>(StringComparer.OrdinalIgnoreCase)
        {
            ["ClypDat Blue"] = new(228.8, 0.4579, 0.1938, 1.5581, -0.0337),
            ["Violet"] = new(265.9, 0.4274, 0.1614, 1.3914, -0.0269),
            ["Emerald"] = new(161.4, 0.4711, 0.1715, 1.1712, -0.0251),
            ["Rose"] = new(333.2, 0.4942, 0.1639, 1.3705, -0.0263),
            ["Amber"] = new(35.4, 0.7844, 0.1753, 1.3132, -0.0279)
        };

    private static readonly IReadOnlyDictionary<string, Color> PresetAccents =
        new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["System"] = Color.Parse("#5864E8"),
            ["ClypDat Blue"] = Color.Parse("#5864E8"),
            ["Violet"] = Color.Parse("#8B5CF6"),
            ["Emerald"] = Color.Parse("#10B981"),
            ["Rose"] = Color.Parse("#F43F5E"),
            ["Amber"] = Color.Parse("#F59E0B")
        };

    // The System source values for the eight named tokens. Themed output is these
    // run through the preset's transform; System itself is the identity, so its
    // rendering is byte-for-byte what the app shipped before themes existed.
    private static readonly (string Key, Color Source)[] NamedTokens =
    {
        ("AppBgBrush", Color.Parse("#0D1116")),
        ("PanelBgBrush", Color.Parse("#101820")),
        ("SurfaceBrush", Color.Parse("#141D24")),
        ("SurfaceRaisedBrush", Color.Parse("#1A242E")),
        ("SurfaceHoverBrush", Color.Parse("#22303D")),
        ("EdgeBrush", Color.Parse("#232F3A")),
        ("EdgeStrongBrush", Color.Parse("#2C3B48")),
        ("DividerBrush", Color.Parse("#1B242D"))
    };

    // SurfaceRamp.axaml keys carry their own System hex ("Surface_1D2A36"), so the
    // source colour is recovered from the key on every apply. Recolouring never
    // reads back a previously themed brush, which is what keeps repeated theme
    // switches from compounding.
    private const string BrushKeyPrefix = "Surface_";
    private const string ColorKeyPrefix = "SurfaceColor_";

    public static IReadOnlyList<ThemeOption> Options { get; } = new[]
    {
        Option("System"), Option("ClypDat Blue"), Option("Violet"),
        Option("Emerald"), Option("Rose"), Option("Amber")
    };

    public static string Normalize(string? preset) =>
        preset is not null && (IsSystem(preset) || Transforms.ContainsKey(preset)) ? preset : "System";

    private static bool IsSystem(string preset) => string.Equals(preset, "System", StringComparison.OrdinalIgnoreCase);

    /// <summary>The accent a preset uses when the Windows accent colour is switched off.</summary>
    public static Color PresetAccent(string preset) =>
        PresetAccents.TryGetValue(Normalize(preset), out var accent) ? accent : PresetAccents["System"];

    public static void Apply(Application application, string preset, Color systemAccent, bool useSystemAccent)
    {
        preset = Normalize(preset);
        var transform = Transforms.TryGetValue(preset, out var t) ? t : ThemeTransform.Identity;
        var accent = useSystemAccent ? systemAccent : PresetAccent(preset);
        var appBackground = Recolor(NamedTokens[0].Source, transform);

        foreach (var (key, source) in NamedTokens) SetBrush(application, key, Recolor(source, transform));
        ApplyRamp(application, transform);

        SetBrush(application, "AccentBrush", accent);
        SetBrush(application, "AccentBrushHover", BlendWithWhite(accent, 0.18));
        SetBrush(application, "AccentSelectedBrush", Blend(accent, appBackground, 0.78));
        SetBrush(application, "AccentSelectedHoverBrush", Blend(accent, appBackground, 0.84));
        SetBrush(application, "AccentSelectedIconBrush", BlendWithWhite(accent, 0.55));
        SetBrush(application, "AccentGameHoverBrush", Blend(accent, appBackground, 0.84));
        SetBrush(application, "AccentFolderBrush", Blend(accent, appBackground, 0.55));
        SetBrush(application, "AccentHoverBrush", Blend(accent, appBackground, 0.84));

        ApplyFluentAccent(application, accent, appBackground);
    }

    // FluentTheme paints ListBoxItem selection, CheckBox ticks and anything else we
    // have not re-templated from SystemAccentColor and its six shades. Left alone
    // they stay Fluent's stock blue, which shows up as a blue selection box inside
    // an Emerald or Rose window. Color is a struct, so these are re-assigned rather
    // than mutated - the assignment is what notifies the DynamicResource consumers
    // inside Fluent's control themes.
    private static void ApplyFluentAccent(Application application, Color accent, Color appBackground)
    {
        application.Resources["SystemAccentColor"] = accent;
        application.Resources["SystemAccentColorLight1"] = BlendWithWhite(accent, 0.20);
        application.Resources["SystemAccentColorLight2"] = BlendWithWhite(accent, 0.40);
        application.Resources["SystemAccentColorLight3"] = BlendWithWhite(accent, 0.60);
        application.Resources["SystemAccentColorDark1"] = Blend(accent, appBackground, 0.20);
        application.Resources["SystemAccentColorDark2"] = Blend(accent, appBackground, 0.40);
        application.Resources["SystemAccentColorDark3"] = Blend(accent, appBackground, 0.60);
    }

    private static void ApplyRamp(Application application, ThemeTransform transform)
    {
        foreach (var resources in MergedDictionaries(application.Resources))
        {
            foreach (var entry in resources)
            {
                if (entry.Key is not string key) continue;
                if (key.StartsWith(BrushKeyPrefix, StringComparison.Ordinal) &&
                    entry.Value is SolidColorBrush brush &&
                    TryParseKeyColor(key, BrushKeyPrefix, out var brushSource))
                {
                    brush.Color = Recolor(brushSource, transform);
                }
                else if (key.StartsWith(ColorKeyPrefix, StringComparison.Ordinal) &&
                         TryParseKeyColor(key, ColorKeyPrefix, out var colorSource))
                {
                    // Color is a struct, so this shadows the ramp entry with a new
                    // value at application level rather than mutating one in place.
                    application.Resources[key] = Recolor(colorSource, transform);
                }
            }
        }
    }

    // A merged entry declared as <ResourceInclude> is not itself a
    // ResourceDictionary - it is a provider wrapping the loaded one - so a plain
    // type test finds nothing and the ramp silently never gets recoloured.
    private static IEnumerable<ResourceDictionary> MergedDictionaries(IResourceDictionary root)
    {
        foreach (var provider in root.MergedDictionaries)
        {
            foreach (var dictionary in Flatten(provider)) yield return dictionary;
        }
    }

    private static IEnumerable<ResourceDictionary> Flatten(IResourceProvider provider)
    {
        switch (provider)
        {
            case ResourceInclude include when include.Loaded is { } loaded:
                foreach (var dictionary in Flatten(loaded)) yield return dictionary;
                break;
            case ResourceDictionary resources:
                yield return resources;
                foreach (var merged in resources.MergedDictionaries)
                {
                    foreach (var dictionary in Flatten(merged)) yield return dictionary;
                }
                break;
        }
    }

    private static bool TryParseKeyColor(string key, string prefix, out Color color)
    {
        color = default;
        var hex = key[prefix.Length..];
        if (hex.Length is not (6 or 8)) return false;
        try
        {
            color = Color.Parse("#" + hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static ThemeOption Option(string id)
    {
        var transform = Transforms.TryGetValue(id, out var t) ? t : ThemeTransform.Identity;
        return new ThemeOption(
            id, id,
            new SolidColorBrush(Recolor(NamedTokens[0].Source, transform)),
            new SolidColorBrush(Recolor(NamedTokens[2].Source, transform)),
            new SolidColorBrush(PresetAccent(id)));
    }

    /// <summary>
    /// Themed brush lookup for the windows built in code rather than XAML, which
    /// have no StaticResource of their own to resolve. Falls back to the literal
    /// so a missing key degrades to the System colour instead of a blank surface.
    /// </summary>
    public static IBrush Brush(string key, string fallbackHex)
    {
        var application = Application.Current;
        if (application is not null && TryFindBrush(application.Resources, key, out var brush)) return brush;
        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    // Application.Resources' indexer does not read through merged dictionaries, so
    // a plain lookup would miss Tokens.axaml and SurfaceRamp.axaml entirely.
    // TryGetResource does traverse them, which is what lets the tokens live in
    // their own files instead of being duplicated into App.axaml to be reachable.
    private static void SetBrush(Application application, string key, Color color)
    {
        if (TryFindBrush(application.Resources, key, out var brush)) brush.Color = color;
    }

    private static bool TryFindBrush(IResourceDictionary resources, string key, out SolidColorBrush brush)
    {
        if (resources.TryGetResource(key, null, out var value) && value is SolidColorBrush found)
        {
            brush = found;
            return true;
        }

        brush = null!;
        return false;
    }

    private static Color Recolor(Color source, ThemeTransform transform)
    {
        if (transform.IsIdentity) return source;

        var (_, saturation, lightness) = ToHsl(source);
        var themedSaturation = Clamp(saturation * transform.SaturationScale + transform.SaturationBias);
        var scaled = Clamp(lightness * transform.LightnessScale + transform.LightnessBias);
        var weight = LightnessWeight(lightness);
        var themedLightness = lightness + (scaled - lightness) * weight;
        return FromHsl(transform.Hue, themedSaturation, themedLightness, source.A);
    }

    private static double LightnessWeight(double lightness)
    {
        if (lightness <= LightnessFullBelow) return 1;
        if (lightness >= LightnessIdentityAbove) return 0;
        return (LightnessIdentityAbove - lightness) / (LightnessIdentityAbove - LightnessFullBelow);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        if (Math.Abs(max - min) < 1e-9) return (0, 0, lightness);

        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double hue;
        if (Math.Abs(max - r) < 1e-9) hue = (g - b) / delta + (g < b ? 6 : 0);
        else if (Math.Abs(max - g) < 1e-9) hue = (b - r) / delta + 2;
        else hue = (r - g) / delta + 4;
        return (hue * 60, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness, byte alpha)
    {
        if (saturation <= 0)
        {
            var grey = (byte)Math.Round(Clamp(lightness) * 255);
            return Color.FromArgb(alpha, grey, grey, grey);
        }

        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        var p = 2 * lightness - q;
        var h = hue / 360.0;
        return Color.FromArgb(
            alpha,
            Channel(p, q, h + 1.0 / 3),
            Channel(p, q, h),
            Channel(p, q, h - 1.0 / 3));
    }

    private static byte Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        double value;
        if (t < 1.0 / 6) value = p + (q - p) * 6 * t;
        else if (t < 1.0 / 2) value = q;
        else if (t < 2.0 / 3) value = p + (q - p) * (2.0 / 3 - t) * 6;
        else value = p;
        return (byte)Math.Round(Clamp(value) * 255);
    }

    private static double Clamp(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private static Color BlendWithWhite(Color color, double amount) => Blend(color, Colors.White, amount);
    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (byte)(from.A + (to.A - from.A) * amount),
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));
}
