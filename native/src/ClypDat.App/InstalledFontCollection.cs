using System.Diagnostics.CodeAnalysis;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace ClypDat.App;

/// <summary>
/// Loads user-installed fonts without fabricating weight, style, or stretch variants.
/// </summary>
internal sealed class InstalledFontCollection : EmbeddedFontCollection
{
    public InstalledFontCollection(Uri key, Uri source)
        : base(key, source)
    {
    }

    public override bool TryCreateSyntheticGlyphTypeface(
        GlyphTypeface glyphTypeface,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
    {
        syntheticGlyphTypeface = null;
        return false;
    }
}
