using Avalonia.Media;

namespace ClypDat.App.ViewModels;

/// <summary>One surface a custom theme's base turns into, for the editor's shades row.</summary>
public sealed record ThemeShade(string Name, Color Color);
