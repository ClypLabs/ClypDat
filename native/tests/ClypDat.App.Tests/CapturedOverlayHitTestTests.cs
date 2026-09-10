using Avalonia;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CapturedOverlayHitTestTests
{
    private static readonly Rect Layer = new(100, 80, 320, 180);

    [Fact]
    public void EmptyCanvasIsNotAMove()
    {
        // The whole overlay window is hit-testable so any layer can be grabbed,
        // so a miss has to stay a miss - otherwise clicking the video would
        // start dragging whichever overlay happened to be nearest.
        Assert.False(CapturedOverlayHitTest.TryHit(Layer, new Point(20, 20), out _));
        Assert.False(CapturedOverlayHitTest.TryHit(Layer, new Point(600, 400), out _));
        Assert.False(CapturedOverlayHitTest.TryHit(default, new Point(0, 0), out _));
    }

    [Fact]
    public void BodyMovesAndCornersResize()
    {
        Assert.True(CapturedOverlayHitTest.TryHit(Layer, Layer.Center, out var move));
        Assert.Equal(VideoOverlayManipulationMode.Move, move);

        Assert.True(CapturedOverlayHitTest.TryHit(Layer, Layer.TopLeft, out var topLeft));
        Assert.Equal(VideoOverlayManipulationMode.TopLeft, topLeft);
        Assert.True(CapturedOverlayHitTest.TryHit(Layer, Layer.TopRight, out var topRight));
        Assert.Equal(VideoOverlayManipulationMode.TopRight, topRight);
        Assert.True(CapturedOverlayHitTest.TryHit(Layer, Layer.BottomLeft, out var bottomLeft));
        Assert.Equal(VideoOverlayManipulationMode.BottomLeft, bottomLeft);
        Assert.True(CapturedOverlayHitTest.TryHit(Layer, Layer.BottomRight, out var bottomRight));
        Assert.Equal(VideoOverlayManipulationMode.BottomRight, bottomRight);
    }

    [Fact]
    public void CornersAreGrabbableFromJustOutsideTheLayer()
    {
        // Matching the Spotify adorner's reach, so both kinds of overlay in the
        // editor grab the same way.
        Assert.True(CapturedOverlayHitTest.TryHit(Layer, new Point(Layer.Left - 4, Layer.Top - 4), out var mode));
        Assert.Equal(VideoOverlayManipulationMode.TopLeft, mode);
        Assert.Equal(SpotifyOverlayAdorner.HandleSize + 4, CapturedOverlayHitTest.HandleReach);
    }

    [Fact]
    public void CornerWinsOverTheBodyItOverlaps()
    {
        // An overlay barely larger than its own handles must still be resizable.
        var small = new Rect(100, 100, 20, 12);
        Assert.True(CapturedOverlayHitTest.TryHit(small, small.TopLeft, out var mode));
        Assert.Equal(VideoOverlayManipulationMode.TopLeft, mode);
    }

    [Fact]
    public void UnselectedLayersExposeOnlyTheirBody()
    {
        Assert.False(CapturedOverlayHitTest.TryHit(Layer, new Point(Layer.Left - 4, Layer.Top - 4), includeHandles: false, out _));
        Assert.True(CapturedOverlayHitTest.TryHit(Layer, Layer.Center, includeHandles: false, out var mode));
        Assert.Equal(VideoOverlayManipulationMode.Move, mode);
    }

    [Fact]
    public void CapturedAdornerDrawsAndHitTestsWithoutRotation()
    {
        // VideoOverlayTransform carries no rotation, so offering a rotate handle
        // would hand back a gesture the transform cannot represent.
        var adorner = new SpotifyOverlayAdorner { CardBounds = Layer, ShowRotationHandle = false };
        var handle = new Point(Layer.Center.X, Layer.Top - SpotifyOverlayAdorner.RotationHandleOffset);

        Assert.False(adorner.TryHitTest(handle, out _));
        Assert.True(adorner.TryHitTest(Layer.Center, out var mode));
        Assert.Equal(SpotifyOverlayDragMode.Move, mode);

        adorner.ShowRotationHandle = true;
        Assert.True(adorner.TryHitTest(handle, out var rotate));
        Assert.Equal(SpotifyOverlayDragMode.Rotate, rotate);

        Assert.False(adorner.TryHitTest(handle, includeHandles: false, out _));
        Assert.True(adorner.TryHitTest(Layer.Center, includeHandles: false, out var body));
        Assert.Equal(SpotifyOverlayDragMode.Move, body);
    }

    [Fact]
    public void DragDeltasAreNormalizedAgainstTheDisplayedVideo()
    {
        // The editor drag feeds VideoOverlayManipulation fractional deltas, not
        // the screen pixels the Spotify manipulation takes.
        var start = new VideoOverlayTransform(.1, .1, .25);
        const double frameWidth = 800, frameHeight = 450;

        var moved = VideoOverlayManipulation.Apply(start, VideoOverlayManipulationMode.Move,
            80 / frameWidth, 45 / frameHeight, frameWidth / frameHeight, VideoOverlayLayout.CameraAspectRatio);

        Assert.Equal(.2, moved.X, 6);
        Assert.Equal(.2, moved.Y, 6);
        Assert.Equal(start.Width, moved.Width, 6);
    }
}
