using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipInputIndexTests
{
    private static InputPhysicalKey Key(ushort scanCode, bool extended = false) => new(scanCode, extended, false);
    private static InputPhysicalKey Mouse(string button) => new(0, false, false, button);

    // W held 1s-3s, left mouse held 2s-2.5s, with a checkpoint at 2s covering
    // both - the shape the recorder actually writes.
    private static InputCaptureIndex Sample() => new(2, null,
    [
        new(1, Key(0x11), true, "Key"),
        new(2, Mouse("MouseLeft"), true, "Mouse"),
        new(2.5, Mouse("MouseLeft"), false, "Mouse"),
        new(3, Key(0x11), false, "Key"),
    ],
    [
        new(0, []),
        new(2, [Key(0x11), Mouse("MouseLeft")]),
    ]);

    [Fact]
    public void ReconstructsWhatWasHeldAtAnyMoment()
    {
        var index = Sample();

        Assert.Empty(ClipInputIndex.PressedAt(index, 0.5));
        Assert.Equal(["KeyW"], ClipInputIndex.PressedAt(index, 1.5));
        Assert.Equal(["KeyW", "MouseLeft"], ClipInputIndex.PressedAt(index, 2.2).OrderBy(x => x));
        Assert.Equal(["KeyW"], ClipInputIndex.PressedAt(index, 2.7));
        Assert.Empty(ClipInputIndex.PressedAt(index, 4));
    }

    [Fact]
    public void SeekingBackwardsRebuildsFromTheNearestCheckpointRatherThanCarryingStateForward()
    {
        var index = Sample();

        // Walk forward past the checkpoint, then jump back behind it.
        Assert.Equal(["KeyW", "MouseLeft"], ClipInputIndex.PressedAt(index, 2.2).OrderBy(x => x));
        Assert.Equal(["KeyW"], ClipInputIndex.PressedAt(index, 1.5));
        Assert.Empty(ClipInputIndex.PressedAt(index, 0));
    }

    [Fact]
    public void AnEscapingAssetPathIsRefused()
    {
        var layer = new ClipOverlayLayer("QWERTY Compact", true, InputIndexPath: @"..\..\outside.json");

        Assert.Null(ClipInputIndex.Load(Path.GetTempPath(), layer));
        Assert.Null(ClipInputIndex.Load(Path.GetTempPath(), null));
    }
}
