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
    public void ExactlyAtATransitionTheKeyIsAlreadyDown()
    {
        var index = Sample();

        Assert.Equal(["KeyW"], ClipInputIndex.PressedAt(index, 1));
        Assert.Empty(ClipInputIndex.PressedAt(index, 3));
    }

    [Fact]
    public void MissingOrUnrecordedHistoryLightsNothing()
    {
        Assert.Empty(ClipInputIndex.PressedAt(null, 1));
        Assert.Empty(ClipInputIndex.PressedAt(new InputCaptureIndex(2, null, [], []), 1));
        Assert.Empty(ClipInputIndex.PressedAt(Sample(), double.NaN));
    }

    [Fact]
    public void UnmappedScanCodesAreDroppedRatherThanLightingTheWrongCap()
    {
        var index = new InputCaptureIndex(2, null, [new(1, Key(0x7F), true, "Key")], [new(0, [])]);

        Assert.Empty(ClipInputIndex.PressedAt(index, 1.5));
    }

    [Fact]
    public void ScanCodesMapToPhysicalPositionSoNonUsLayoutsLightTheRightCap()
    {
        // The key beside Tab is drawn "Q" on QWERTY and "A" on AZERTY, and
        // reports the same scan code on both.
        Assert.Equal("KeyQ", InputKeyMap.Code(0x10, false));
        Assert.Equal("KeyW", InputKeyMap.Code(0x11, false));
        Assert.Equal("Digit1", InputKeyMap.Code(0x02, false));
        Assert.Equal("Space", InputKeyMap.Code(0x39, false));
        Assert.Equal("Backslash", InputKeyMap.Code(0x2B, false));

        // The E0 flag is part of the key: these bytes are numpad keys without it.
        Assert.Equal("ControlLeft", InputKeyMap.Code(0x1D, false));
        Assert.Equal("ControlRight", InputKeyMap.Code(0x1D, true));
        Assert.Equal("ArrowUp", InputKeyMap.Code(0x48, true));
        Assert.Equal("Numpad8", InputKeyMap.Code(0x48, false));
        Assert.Equal("AltRight", InputKeyMap.Code(0x38, true));
        Assert.Equal("MetaLeft", InputKeyMap.Code(0x5B, true));

        Assert.Null(InputKeyMap.Code(0x7F, false));
    }

    [Fact]
    public void TheOnDiskShapeStillRoundTripsAfterMovingTheRecordsOutOfTheRecorder()
    {
        // Sidecars already written used default serializer options against the
        // nested records; nesting is not part of the JSON but property names are.
        const string onDisk = """
            {"Version":2,"MissingHistory":null,
             "Transitions":[{"Seconds":1.5,"Key":{"ScanCode":17,"E0":false,"E1":false,"MouseButton":null},"Down":true,"Kind":"Key"}],
             "Checkpoints":[{"Seconds":0,"Down":[]}]}
            """;

        var index = JsonSerializer.Deserialize<InputCaptureIndex>(onDisk)!;

        Assert.Equal(2, index.Version);
        Assert.Equal(["KeyW"], ClipInputIndex.PressedAt(index, 2));
        Assert.Equal(onDisk.Replace(" ", "").Replace("\r", "").Replace("\n", ""),
            JsonSerializer.Serialize(index).Replace(" ", ""));
    }

    [Fact]
    public void AnEscapingAssetPathIsRefused()
    {
        var layer = new ClipOverlayLayer("QWERTY Compact", true, InputIndexPath: @"..\..\outside.json");

        Assert.Null(ClipInputIndex.Load(Path.GetTempPath(), layer));
        Assert.Null(ClipInputIndex.Load(Path.GetTempPath(), null));
    }
}
