using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// One preset, rendered as its own swatch. Every brush here is that preset's
/// colour rather than the active theme's - a dark tile has to stay readable
/// while a light theme is applied, and the other way round.
/// </summary>
public sealed record ThemeOption(
    string Id,
    string Label,
    IBrush AppBackgroundBrush,
    IBrush SurfaceBrush,
    IBrush AccentBrush,
    IBrush LabelTextBrush);

/// <summary>Owns ClypDat's mutable dark-theme resource palette.</summary>
internal static class AppThemeService
{
    // Every themed colour in the app - the named tokens in Tokens.axaml and the
    // ~210 shades in ThemeRamp.axaml alike - is ClypDat's authored System palette
    // run through one transform. Nothing is hand-picked per theme except the
    // accent, so a shade that has no named token still lands in the right place
    // instead of being flattened onto the nearest one.
    //
    // The dark transforms were fitted against the hand-picked palettes the preset
    // themes originally shipped with; every channel reproduces to within 4/255, so
    // deriving them is not a visible change.
    private enum ColorRole
    {
        // Backgrounds, borders and fills. Re-hued by the dark presets.
        Surface,
        // Text, icons and glyphs. Untouched by the dark presets - text colours
        // stay put for readability - and inverted by the light ones.
        Text,
        // Greens, reds and ambers that carry meaning. Never re-hued; only
        // inverted for light mode, so they stay legible on a pale surface.
        Semantic
    }

    private sealed record ThemeTransform(
        double Hue,
        double SaturationScale, double SaturationBias,
        double LightnessScale, double LightnessBias,
        bool IsLight = false)
    {
        public static readonly ThemeTransform Identity = new(-1, 1, 0, 1, 0);
        public bool IsIdentity => Hue < 0 && !IsLight;
    }

    // Lightness is scaled fully across the range the named tokens occupy (up to
    // ~0.23) and eased back to the source value by 0.40. Without that taper the
    // >1 slope would drag the brightest ramp entries - hairline borders around
    // 0.38 - into mid-greys, which reads as a different design rather than a tint.
    private const double LightnessFullBelow = 0.23;
    private const double LightnessIdentityAbove = 0.40;

    // Light mode inverts lightness, so the ramp's ordering survives: the darkest
    // surface becomes the lightest, the brightest text becomes the darkest.
    //
    // Text and semantic colours are compressed toward the dark end on the way
    // back, because a straight inversion is not enough for them. A muted label at
    // 43% lightness inverts to 57% - still mid-grey, now sitting on a 93% page
    // instead of a 7% one, so it loses most of its contrast. Semantic colours have
    // the same problem: inverted, the caution amber comes back *lighter* than it
    // started. Surfaces need no such help; they are meant to be near the page.
    private const double LightSurfaceSaturationScale = 1.0;
    private const double LightTextLightnessScale = 0.75;
    private const double LightSemanticLightnessScale = 0.85;

    // A custom theme's base used to be painted as picked. A UI ground is not a
    // colour you choose off a spectrum, though - ClypDat's own is #0D1116, and
    // the authored dark family around it sits at S 0.24-0.29 with the page at
    // L 0.069 (#141D24 0.286/0.110, #22303D 0.284/0.186, #2C3B48 0.241/0.227).
    // A mid green off the spectrum is S 0.592 at L 0.404: twice the chroma and
    // six times the lightness of the thing it replaces, painted across every
    // panel in the app.
    //
    // So the pick supplies the hue, and its chroma and lightness are clamped
    // into the band the shipped palette already occupies. Clamped, not scaled:
    // a pick already inside the band comes back untouched, which is what keeps
    // a custom theme built on #0D1116 rendering byte-for-byte as System. The
    // colour the user chose is still what the swatch and the saved theme show -
    // this is a render step, not an edit of their choice.
    private const double CustomSurfaceMaxSaturation = 0.30;
    private const double CustomDarkMinLightness = 0.05;
    private const double CustomDarkMaxLightness = 0.13;

    // Light mode is the same band inverted: 1 - 0.069 = 0.931 is where the light
    // presets' page lands, and RecolorCustom negates its delta there, so the
    // ramp reproduces stock light from an anchor in this range.
    private const double CustomLightMinLightness = 0.90;
    private const double CustomLightMaxLightness = 0.97;

    // Which band a pick lands in. A light theme needs a pale colour, and pale
    // means two things at once - bright AND washed out. Testing brightness alone
    // does not work whichever measure you use:
    //
    //   Relative luminance weights green at 0.7152, so #22D011 - a fully
    //   saturated green - scores 0.455 and a 0.45 threshold turns the app white.
    //   Pure yellow scores 0.928 and pure cyan 0.787, and none of the three is a
    //   colour anything can be read on.
    //
    //   HSL lightness alone has the opposite fault: it puts every fully
    //   saturated hue at exactly 0.5, so a genuinely pale #B0B0B0 at 0.69 and a
    //   pale green at 0.71 sit barely above colours that must stay dark.
    //
    // So both, with chroma - S scaled by how far L is from the extremes, which
    // is what separates "pale" from "vivid" - carrying the second half. For
    // reference the two grounds this has to agree with, #0D1116 and its light
    // inverse, both sit at chroma 0.035.
    private const double CustomLightMinPickLightness = 0.62;
    private const double CustomLightMaxPickChroma = 0.35;

    private static readonly IReadOnlyDictionary<string, ThemeTransform> Transforms =
        new Dictionary<string, ThemeTransform>(StringComparer.OrdinalIgnoreCase)
        {
            ["ClypDat Blue"] = new(228.8, 0.4579, 0.1938, 1.5581, -0.0337),
            ["Berry"] = new(266.0, 1.0, 0.6, 1.3914, -0.0269),
            ["Emerald"] = new(161.4, 0.4711, 0.1715, 1.1712, -0.0251),
            ["Rose"] = new(333.2, 0.4942, 0.1639, 1.3705, -0.0263),
            ["Amber"] = new(35.4, 0.7844, 0.1753, 1.3132, -0.0279),

            // Light presets reuse their dark sibling's hue. Scale and bias are
            // unused for them - Recolor inverts lightness outright instead.
            ["Light"] = new(209.0, 1, 0, 1, 0, IsLight: true),
            ["Light Blue"] = new(228.8, 1, 0, 1, 0, IsLight: true),
            ["Light Berry"] = new(265.9, 1, 0, 1, 0, IsLight: true),
            ["Light Emerald"] = new(161.4, 1, 0, 1, 0, IsLight: true),
            ["Light Rose"] = new(333.2, 1, 0, 1, 0, IsLight: true),
            ["Light Amber"] = new(35.4, 1, 0, 1, 0, IsLight: true)
        };

    private static readonly IReadOnlyDictionary<string, Color> PresetAccents =
        new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["System"] = Color.Parse("#5864E8"),
            ["ClypDat Blue"] = Color.Parse("#5864E8"),
            ["Berry"] = Color.Parse("#612da4"),
            ["Emerald"] = Color.Parse("#10B981"),
            ["Rose"] = Color.Parse("#F43F5E"),
            ["Amber"] = Color.Parse("#F59E0B"),

            // Light-mode accents run darker than their dark-mode counterparts: the
            // same indigo that reads as bright on #0D1116 is washed out on a
            // near-white surface.
            ["Light"] = Color.Parse("#4650C9"),
            ["Light Blue"] = Color.Parse("#4650C9"),
            ["Light Berry"] = Color.Parse("#7343E0"),
            ["Light Emerald"] = Color.Parse("#0E9268"),
            ["Light Rose"] = Color.Parse("#D42145"),
            ["Light Amber"] = Color.Parse("#B87608")
        };

    private static readonly string[] LightPresetOrder =
        { "Light", "Light Blue", "Light Berry", "Light Emerald", "Light Rose", "Light Amber" };

    // The System source values for the named tokens, with the role each is
    // recoloured under. System itself is the identity transform, so its rendering
    // is byte-for-byte what the app shipped before themes existed.
    private static readonly (string Key, Color Source, ColorRole Role)[] NamedTokens =
    {
        ("AppBgBrush", Color.Parse("#0D1116"), ColorRole.Surface),
        ("PanelBgBrush", Color.Parse("#101820"), ColorRole.Surface),
        ("SurfaceBrush", Color.Parse("#141D24"), ColorRole.Surface),
        ("SurfaceRaisedBrush", Color.Parse("#1A242E"), ColorRole.Surface),
        ("SurfaceHoverBrush", Color.Parse("#22303D"), ColorRole.Surface),
        ("EdgeBrush", Color.Parse("#232F3A"), ColorRole.Surface),
        ("EdgeStrongBrush", Color.Parse("#2C3B48"), ColorRole.Surface),
        ("DividerBrush", Color.Parse("#1B242D"), ColorRole.Surface),

        ("TextStrongBrush", Color.Parse("#EDF4FB"), ColorRole.Text),
        ("TextBrush", Color.Parse("#D2DEEC"), ColorRole.Text),
        ("TextSubtleBrush", Color.Parse("#9FB2C6"), ColorRole.Text),
        ("TextMutedBrush", Color.Parse("#6B7C8C"), ColorRole.Text),
        ("LabelBrush", Color.Parse("#5C6D7E"), ColorRole.Text),

        ("LinkBrush", Color.Parse("#78B8FF"), ColorRole.Semantic),
        ("CautionBrush", Color.Parse("#E5A00D"), ColorRole.Semantic),
        ("DangerBrush", Color.Parse("#D85E61"), ColorRole.Semantic)
    };

    // ThemeRamp.axaml keys carry their role and their own System hex
    // ("Surface_1D2A36"), so both are recovered from the key on every apply.
    // Recolouring never reads back a previously themed brush, which is what keeps
    // repeated theme switches from compounding.
    private static readonly (string Prefix, ColorRole Role, bool IsColor)[] RampPrefixes =
    {
        ("SurfaceColor_", ColorRole.Surface, true),
        ("TextColor_", ColorRole.Text, true),
        ("SemanticColor_", ColorRole.Semantic, true),
        ("Surface_", ColorRole.Surface, false),
        ("Text_", ColorRole.Text, false),
        ("Semantic_", ColorRole.Semantic, false)
    };

    public static IReadOnlyList<ThemeOption> Options { get; } = new[]
    {
        Option("System"), Option("ClypDat Blue"), Option("Berry"),
        Option("Emerald"), Option("Rose"), Option("Amber")
    };

    public static IReadOnlyList<ThemeOption> LightOptions { get; } =
        LightPresetOrder.Select(Option).ToArray();

    public static bool IsLight(string preset) =>
        Transforms.TryGetValue(Normalize(preset), out var transform) && transform.IsLight;

    public static string Normalize(string? preset)
    {
        if (string.Equals(preset, "Violet", StringComparison.OrdinalIgnoreCase)) return "Berry";
        if (string.Equals(preset, "Light Violet", StringComparison.OrdinalIgnoreCase)) return "Light Berry";
        return preset is not null && (IsSystem(preset) || Transforms.ContainsKey(preset)) ? preset : "System";
    }

    /// <summary>
    /// The System preset is the only one with no accent of its own - it is ClypDat's
    /// neutral palette following Windows - so it is also the only one the Windows
    /// accent is the natural default for.
    /// </summary>
    public static bool IsSystem(string preset) => string.Equals(preset, "System", StringComparison.OrdinalIgnoreCase);

    /// <summary>The accent a preset uses when the Windows accent colour is switched off.</summary>
    public static Color PresetAccent(string preset) =>
        PresetAccents.TryGetValue(Normalize(preset), out var accent) ? accent : PresetAccents["System"];

    public static void Apply(Application application, string preset, Color systemAccent, bool useSystemAccent,
        CustomThemeSettings? customTheme = null)
    {
        preset = Normalize(preset);
        var transform = Transforms.TryGetValue(preset, out var t) ? t : ThemeTransform.Identity;
        var isCustom = customTheme is not null;
        // Everything downstream reads the damped ground, never the raw pick: the
        // colour the theme is painted in and the colour the accent has to stay
        // legible against are the same colour, and it is this one.
        var (customBase, customLight) = isCustom ? SurfaceBase(Color.Parse(customTheme!.BaseColor)) : (default, false);
        var accent = isCustom ? AdjustAccent(Color.Parse(customTheme!.AccentColor), customBase) : useSystemAccent ? systemAccent : PresetAccent(preset);
        var appBackground = isCustom ? customBase : Recolor(NamedTokens[0].Source, ColorRole.Surface, transform);

        // FluentTheme swaps its whole control-theme resource set on this. Without
        // it every control we have not re-templated - TextBox, ComboBox popups,
        // CheckBox - keeps its dark chrome on a near-white page.
        application.RequestedThemeVariant = isCustom ? (customLight ? ThemeVariant.Light : ThemeVariant.Dark) : transform.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;

        foreach (var (key, source, role) in NamedTokens)
        {
            SetBrush(application, key, isCustom ? RecolorCustom(source, role, customBase, customLight) : Recolor(source, role, transform));
        }
        ApplyRamp(application, transform, isCustom ? customBase : null, customLight);

        SetBrush(application, "AccentBrush", accent);
        SetBrush(application, "AccentBrushHover", (isCustom ? customLight : transform.IsLight)
            ? Blend(accent, appBackground, 0.22)
            : BlendWithWhite(accent, 0.18));
        SetBrush(application, "AccentSelectedBrush", Blend(accent, appBackground, 0.78));
        SetBrush(application, "AccentSelectedHoverBrush", Blend(accent, appBackground, 0.84));
        SetBrush(application, "AccentSelectedIconBrush", (isCustom ? customLight : transform.IsLight)
            ? Blend(accent, Colors.Black, 0.25)
            : BlendWithWhite(accent, 0.55));
        SetBrush(application, "AccentGameHoverBrush", Blend(accent, appBackground, 0.84));
        SetBrush(application, "AccentFolderBrush", Blend(accent, appBackground, 0.55));
        SetBrush(application, "AccentHoverBrush", Blend(accent, appBackground, 0.84));

        ApplyFluentAccent(application, accent, appBackground);
        SetBrush(application, "AccentForegroundBrush", BestForeground(accent));
        ApplyLogo(application, isCustom ? customLight : transform.IsLight);
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

    // The app mark is a near-white shape with a black outline, which disappears
    // against a light page. The light-mode assets are its exact luminance
    // inverse - black shape, white outline - so the two read identically on
    // their own backgrounds. Loaded once each and cached; a theme switch swaps
    // which pair the resources point at, it does not re-decode.
    private static readonly Dictionary<string, Bitmap> LogoCache = new(StringComparer.Ordinal);
    private static bool _isLightTheme;

    /// <summary>The app mark for the active theme. Use for surfaces the app paints itself.</summary>
    public static Bitmap CurrentLogo(bool large) => Logo(large, _isLightTheme);

    private static Bitmap Logo(bool large, bool light)
    {
        var name = $"clypdat-icon-{(large ? 256 : 24)}{(light ? "-light" : string.Empty)}.png";
        if (LogoCache.TryGetValue(name, out var cached)) return cached;
        var bitmap = new Bitmap(AssetLoader.Open(new Uri($"avares://ClypDat/Assets/{name}")));
        LogoCache[name] = bitmap;
        return bitmap;
    }

    private static void ApplyLogo(Application application, bool isLight)
    {
        _isLightTheme = isLight;
        application.Resources["AppLogoSmall"] = Logo(false, isLight);
        application.Resources["AppLogoLarge"] = Logo(true, isLight);
    }

    private static void ApplyRamp(Application application, ThemeTransform transform, Color? customBase = null, bool customLight = false)
    {
        // Keys only, then resolve each one back through TryGetResource. A
        // dictionary loaded from XAML can hold its entries as deferred content
        // rather than the finished object, so reading entry.Value directly finds
        // something that is not a brush and skips it - while StaticResource later
        // materializes an instance of its own. That is why the named tokens, which
        // have always gone through TryGetResource, themed correctly at startup
        // while the ramp did not, and why the ramp appeared to fix itself as soon
        // as a theme was picked by hand.
        foreach (var key in RampKeys(application))
        {
            if (!TryMatchRamp(key, out var role, out var isColor, out var source)) continue;

            var themed = customBase is { } baseColor ? RecolorCustom(source, role, baseColor, customLight) : Recolor(source, role, transform);
            if (isColor)
            {
                // Color is a struct, so this shadows the ramp entry with a new
                // value at application level rather than mutating one in place.
                application.Resources[key] = themed;
            }
            else if (TryFindBrush(application.Resources, key, out var brush))
            {
                brush.Color = themed;
            }
        }
    }

    private static IReadOnlyList<string> RampKeys(Application application)
    {
        if (_rampKeys is not null) return _rampKeys;

        var keys = new List<string>();
        foreach (var resources in MergedDictionaries(application.Resources))
        {
            foreach (var entry in resources)
            {
                if (entry.Key is string key && RampPrefixes.Any(p => key.StartsWith(p.Prefix, StringComparison.Ordinal)))
                {
                    keys.Add(key);
                }
            }
        }

        _rampKeys = keys;
        // The ramp is what every non-token surface in the app resolves through,
        // and a silent zero here reads in the UI as "the theme only half applied".
        // Cheap enough to state once per launch.
        AppLog.Info($"Theme ramp: {keys.Count} keys registered.");
        return keys;
    }

    // The ramp is a generated file; its key set cannot change while the app runs.
    private static IReadOnlyList<string>? _rampKeys;

    private static bool TryMatchRamp(string key, out ColorRole role, out bool isColor, out Color source)
    {
        foreach (var (prefix, prefixRole, prefixIsColor) in RampPrefixes)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!TryParseKeyColor(key, prefix, out source)) continue;
            role = prefixRole;
            isColor = prefixIsColor;
            return true;
        }

        role = ColorRole.Surface;
        isColor = false;
        source = default;
        return false;
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
        var text = NamedTokens.First(token => token.Key == "TextBrush");
        return new ThemeOption(
            id, id,
            new SolidColorBrush(Recolor(NamedTokens[0].Source, ColorRole.Surface, transform)),
            new SolidColorBrush(Recolor(NamedTokens[2].Source, ColorRole.Surface, transform)),
            new SolidColorBrush(PresetAccent(id)),
            new SolidColorBrush(Recolor(text.Source, ColorRole.Text, transform)));
    }

    /// <summary>
    /// Themed colour lookup for the code-built surfaces that need a Color rather
    /// than a brush - gradient stops, pens built per render. Live wherever the
    /// call is re-evaluated; a value cached in a static field is a snapshot, so
    /// prefer <see cref="Brush"/> where a brush will do.
    /// </summary>
    public static Color ThemeColor(string key, string fallbackHex) =>
        Brush(key, fallbackHex) is SolidColorBrush brush
            ? brush.Color
            : Color.Parse(fallbackHex);

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

    private static Color Recolor(Color source, ColorRole role, ThemeTransform transform)
    {
        if (transform.IsIdentity) return source;

        var (hue, saturation, lightness) = ToHsl(source);

        if (transform.IsLight)
        {
            // Inverting lightness is what makes a light theme out of a palette
            // authored dark: the ordering survives, so a raised surface stays
            // distinguishable from the page and strong text stays strongest.
            var invertedLightness = 1 - lightness;
            return role switch
            {
                ColorRole.Surface => FromHsl(
                    transform.Hue,
                    Clamp(saturation * LightSurfaceSaturationScale),
                    invertedLightness,
                    source.A),
                // Text and semantic colours keep their own hue. Re-hueing text
                // would tint the whole page; re-hueing a red would stop it
                // meaning "danger".
                ColorRole.Text => FromHsl(
                    hue, saturation, invertedLightness * LightTextLightnessScale, source.A),
                _ => FromHsl(
                    hue, saturation, invertedLightness * LightSemanticLightnessScale, source.A)
            };
        }

        // The dark presets tint surfaces only. Text stays as authored, and a
        // semantic green has to stay green to still say what it says.
        if (role != ColorRole.Surface) return source;

        var themedSaturation = Clamp(saturation * transform.SaturationScale + transform.SaturationBias);
        var scaled = Clamp(lightness * transform.LightnessScale + transform.LightnessBias);
        var weight = LightnessWeight(lightness);
        var themedLightness = lightness + (scaled - lightness) * weight;
        return FromHsl(transform.Hue, themedSaturation, themedLightness, source.A);
    }

    // Custom themes retain ClypDat's authored surface hierarchy. Only its
    // hue/lightness anchor changes; text is chosen from true contrast rather
    // than assuming every custom base is dark.
    /// <summary>
    /// The ground a custom theme is actually painted in, and whether that ground
    /// is a light one. The pick contributes its hue; its chroma and lightness are
    /// clamped into the band ClypDat's own palette occupies, because a colour
    /// chosen off a spectrum is a colour, not a UI background. Math.Clamp rather
    /// than the file's own Clamp, which is a 0..1 clamp and would read
    /// confusingly here.
    /// </summary>
    private static (Color Ground, bool Light) SurfaceBase(Color picked)
    {
        var (hue, saturation, lightness) = ToHsl(picked);
        var chroma = saturation * (1 - Math.Abs(2 * lightness - 1));
        var light = lightness >= CustomLightMinPickLightness && chroma <= CustomLightMaxPickChroma;
        var ground = FromHsl(
            hue,
            Math.Min(saturation, CustomSurfaceMaxSaturation),
            Math.Clamp(lightness,
                light ? CustomLightMinLightness : CustomDarkMinLightness,
                light ? CustomLightMaxLightness : CustomDarkMaxLightness),
            picked.A);
        return (ground, light);
    }

    private static Color RecolorCustom(Color source, ColorRole role, Color baseColor, bool light)
    {
        if (role == ColorRole.Text)
        {
            // Which pole to blend toward is a contrast question, so ask the helper
            // that answers contrast questions rather than reusing the light flag -
            // the two agree on ordinary grounds and the helper is right on the ones
            // near the boundary.
            var foreground = BestForeground(baseColor);
            var (_, _, textLightness) = ToHsl(source);
            var alpha = textLightness < .48 ? .58 : textLightness < .66 ? .74 : 1d;
            // Body text carries WCAG AA; the muted tiers only have to stay legible,
            // and holding them to 4.5 as well would flatten the whole ramp into one
            // shade. This is the check the Semantic role has always had and the
            // Text role never did, which is how muted labels ended up tinted to
            // within a shade of the surface they sit on.
            var minimum = alpha >= 1d ? 4.5 : 3;
            return EnsureContrast(Blend(baseColor, foreground, alpha), baseColor, minimum);
        }

        if (role == ColorRole.Semantic)
            return EnsureContrast(source, baseColor, 3);

        var (hue, saturation, baseLightness) = ToHsl(baseColor);
        var (_, _, sourceLightness) = ToHsl(source);
        var (_, _, appLightness) = ToHsl(NamedTokens[0].Source);
        var delta = sourceLightness - appLightness;
        var target = Clamp(baseLightness + (light ? -delta : delta));
        return FromHsl(hue, saturation, target, source.A);
    }

    private static Color AdjustAccent(Color accent, Color background) => EnsureContrast(accent, background, 3);

    private static Color EnsureContrast(Color color, Color background, double minimum)
    {
        if (Contrast(color, background) >= minimum) return color;
        var (hue, saturation, lightness) = ToHsl(color);
        var towardsWhite = Contrast(Colors.White, background) >= Contrast(Colors.Black, background);
        for (var step = 1; step <= 100; step++)
        {
            var candidate = FromHsl(hue, saturation, Clamp(lightness + (towardsWhite ? step : -step) / 100d), color.A);
            if (Contrast(candidate, background) >= minimum) return candidate;
        }
        return towardsWhite ? Colors.White : Colors.Black;
    }

    private static Color BestForeground(Color background) =>
        Contrast(Colors.Black, background) >= Contrast(Colors.White, background) ? Colors.Black : Colors.White;

    private static double Contrast(Color first, Color second)
    {
        var high = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var low = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (high + .05) / (low + .05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
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
