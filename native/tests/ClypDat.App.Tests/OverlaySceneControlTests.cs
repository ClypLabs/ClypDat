using ClypDat.App.Controls;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class OverlaySceneControlTests
{
    [Fact]
    public void EditorHasOneInteractiveSceneForRecordedCameraAndInput()
    {
        var scene = typeof(KeyboardOverlayPreview).Assembly.GetType("ClypDat.App.Controls.OverlaySceneControl");
        Assert.NotNull(scene);
        Assert.NotNull(scene.GetMethod("SetPeripherals"));
        Assert.NotNull(scene.GetMethod("BeginPointerGesture"));
    }
}
