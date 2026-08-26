using Avalonia.Controls;

namespace ClypDat.App.Controls;

/// <summary>
/// The app's loading mark: ClypDat's own hexagons, counter-rotating, with an
/// orbiting arc. Purely declarative - every layer is a XAML animation, so this
/// class exists only to give the control a type.
///
/// Scales to whatever size the caller gives it (the whole thing is vector inside
/// a Viewbox) and draws in <c>Foreground</c>, so one property tints all of it.
/// <c>assets/clypdat-loader.svg</c> is the same mark for non-Avalonia surfaces.
/// </summary>
public sealed partial class ClypDatLoader : UserControl
{
    public ClypDatLoader() => InitializeComponent();
}
