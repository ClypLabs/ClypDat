using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.VisualTree;
using System.Numerics;

namespace ClypDat.App.Controls;

/// <summary>
/// The app's loading mark: ClypDat's own hexagons, counter-rotating, with an
/// orbiting arc. Its motion is driven by Avalonia's composition renderer so it
/// remains alive while the UI thread constructs and lays out the main window.
///
/// Scales to whatever size the caller gives it (the whole thing is vector inside
/// a Viewbox) and draws in <c>Foreground</c>, so one property tints all of it.
/// <c>assets/clypdat-loader.svg</c> is the same mark for non-Avalonia surfaces.
/// </summary>
public sealed partial class ClypDatLoader : UserControl
{
    private bool _isAttached;
    private bool _animationsStarted;

    public ClypDatLoader()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => StartAnimations();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _animationsStarted = false;
        StartAnimations();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _animationsStarted = false;
        StopAnimation(Glow, "Opacity");
        StopAnimation(Glow, "Scale");
        StopAnimation(Ring, "RotationAngle");
        StopAnimation(OuterHexagon, "RotationAngle");
        StopAnimation(InnerHexagon, "RotationAngle");
        base.OnDetachedFromVisualTree(e);
    }

    private void StartAnimations()
    {
        if (_animationsStarted || !_isAttached || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        _animationsStarted = true;
        StartRotation(Ring, TimeSpan.FromSeconds(1.6), 0, TwoPi);
        StartRotation(OuterHexagon, TimeSpan.FromSeconds(6), 0, TwoPi);
        StartRotation(InnerHexagon, TimeSpan.FromSeconds(4.5), TwoPi, 0);
        StartGlow();
    }

    private void StartGlow()
    {
        var visual = ElementComposition.GetElementVisual(Glow);
        if (visual is null)
            return;

        visual.CenterPoint = CentreOf(Glow);
        visual.Opacity = 0.18f;
        visual.Scale = new Vector3D(0.8, 0.8, 1);

        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.Duration = TimeSpan.FromSeconds(2.2);
        opacity.IterationBehavior = AnimationIterationBehavior.Forever;
        opacity.Direction = PlaybackDirection.Alternate;
        opacity.InsertKeyFrame(0, 0.18f, new SineEaseInOut());
        opacity.InsertKeyFrame(1, 0.5f, new SineEaseInOut());
        visual.StartAnimation(opacity.Target, opacity);

        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.Duration = TimeSpan.FromSeconds(2.2);
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        scale.Direction = PlaybackDirection.Alternate;
        scale.InsertKeyFrame(0, new Vector3(0.8f, 0.8f, 1), new SineEaseInOut());
        scale.InsertKeyFrame(1, new Vector3(1.1f, 1.1f, 1), new SineEaseInOut());
        visual.StartAnimation(scale.Target, scale);
    }

    private static void StartRotation(Control control, TimeSpan duration, float from, float to)
    {
        var visual = ElementComposition.GetElementVisual(control);
        if (visual is null)
            return;

        visual.CenterPoint = CentreOf(control);
        visual.RotationAngle = from;

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Target = "RotationAngle";
        animation.Duration = duration;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        animation.InsertKeyFrame(0, from, new LinearEasing());
        animation.InsertKeyFrame(1, to, new LinearEasing());
        visual.StartAnimation(animation.Target, animation);
    }

    private static void StopAnimation(Control control, string property) =>
        ElementComposition.GetElementVisual(control)?.StopAnimation(property);

    private static Vector3D CentreOf(Control control) =>
        new(control.Bounds.Width / 2, control.Bounds.Height / 2, 0);

    private const float TwoPi = (float)(2 * Math.PI);
}
