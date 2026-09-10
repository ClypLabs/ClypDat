using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CustomKeyboardLayoutTests
{
    private static IReadOnlyList<string> Labels(CustomKeyboardBoardShape board, int row) =>
        board.Rows[row].Select(cap => cap.Label).ToArray();

    [Fact]
    public void ChosenKeysPackTogetherInsteadOfLeavingHolesWhereTheOthersWere()
    {
        // W above A S D is the whole point: on a real board W sits one key right
        // of A, and a positional overlay would be mostly empty space.
        var board = CustomKeyboardBoard.Pack(["KeyW", "KeyA", "KeyS", "KeyD"], includeMouse: true);

        Assert.Equal(2, board.Rows.Count);
        Assert.Equal(["W"], Labels(board, 0));
        Assert.Equal(["A", "S", "D"], Labels(board, 1));
    }

    [Fact]
    public void RowsAndColumnsFollowTheKeyboardNotThePressOrder()
    {
        var board = CustomKeyboardBoard.Pack(["KeyP", "Digit1", "KeyZ", "KeyQ", "KeyA"], includeMouse: false);

        Assert.Equal(["1"], Labels(board, 0));
        Assert.Equal(["Q", "P"], Labels(board, 1));
        Assert.Equal(["A"], Labels(board, 2));
        Assert.Equal(["Z"], Labels(board, 3));
    }

    [Fact]
    public void ArrowsBecomeTheirOwnClusterUnlessTheyAreTheWholeSet()
    {
        var withLetters = CustomKeyboardBoard.Pack(["KeyW", "ArrowUp", "ArrowLeft", "ArrowDown", "ArrowRight"], true);
        Assert.Single(withLetters.Rows);
        Assert.Equal(2, withLetters.Cluster.Count);

        // Arrows alone are a board in their own right - that is the Arrows preset.
        var alone = CustomKeyboardBoard.Pack(["ArrowUp", "ArrowLeft", "ArrowDown", "ArrowRight"], true);
        Assert.Equal(2, alone.Rows.Count);
        Assert.Empty(alone.Cluster);
    }

    [Fact]
    public void WideKeysKeepTheirWidthSoThePackedBlockStillReadsAsAKeyboard()
    {
        var board = CustomKeyboardBoard.Pack(["ShiftLeft", "Space", "KeyA"], includeMouse: true);

        var caps = board.Rows.SelectMany(row => row).ToArray();
        Assert.Equal(2, caps.Single(cap => cap.Code == "ShiftLeft").Units);
        Assert.Equal(4, caps.Single(cap => cap.Code == "Space").Units);
        Assert.Equal(1, caps.Single(cap => cap.Code == "KeyA").Units);
    }

    [Fact]
    public void AspectComesFromTheBoardsOwnShape()
    {
        // Presets declare a canvas in the catalog; a board assembled at runtime
        // has none, and every placement site works from this number.
        var narrow = CustomKeyboardBoard.Pack(["KeyW", "KeyA", "KeyS", "KeyD"], includeMouse: true);
        var wide = CustomKeyboardBoard.Pack(
            ["KeyQ", "KeyW", "KeyE", "KeyR", "KeyT", "KeyY", "KeyU", "KeyI", "KeyO", "KeyP"], includeMouse: true);

        Assert.True(CustomKeyboardBoard.AspectRatio(wide) > CustomKeyboardBoard.AspectRatio(narrow));
        Assert.True(CustomKeyboardBoard.AspectRatio(narrow) > 0);
    }

    [Fact]
    public void AnEmptyOrMouseOnlySetStillMeasures()
    {
        // The renderer cannot measure a board with no rows, and a mouse sized off
        // a zero-height board would come out invisible.
        var empty = CustomKeyboardBoard.Pack([], includeMouse: false);
        var mouseOnly = CustomKeyboardBoard.Pack([], includeMouse: true);

        Assert.Empty(empty.Rows);
        Assert.True(double.IsFinite(CustomKeyboardBoard.AspectRatio(empty)));
        Assert.True(CustomKeyboardBoard.AspectRatio(mouseOnly) > 0);
    }

    [Fact]
    public void LabelsComeFromThePhysicalPositionSoASetIsLayoutNeutral()
    {
        Assert.Equal("Q", CustomKeyboardBoard.LabelFor("KeyQ"));
        Assert.Equal("1", CustomKeyboardBoard.LabelFor("Digit1"));
        Assert.Equal("Shift", CustomKeyboardBoard.LabelFor("ShiftLeft"));
        Assert.Equal("Ctrl", CustomKeyboardBoard.LabelFor("ControlRight"));
        Assert.Equal("↑", CustomKeyboardBoard.LabelFor("ArrowUp"));
    }

    [Fact]
    public void SanitizeDropsUnknownPositionsDuplicatesAndOverflow()
    {
        var keys = CustomKeyboardLibrary.Sanitize(["KeyW", "keyw", "NotAKey", "", "MouseLeft", "KeyA"]);

        Assert.Equal(["KeyW", "KeyA"], keys);
        var everyLetterTwice = Enumerable.Range(0, 26).Select(i => $"Key{(char)('A' + i)}")
            .Concat(Enumerable.Range(0, 26).Select(i => $"key{(char)('a' + i)}"))
            .Concat(Enumerable.Range(0, 10).Select(i => $"Digit{i}"))
            .Concat(Enumerable.Range(1, 12).Select(i => $"F{i}"))
            .Concat(Enumerable.Range(0, 10).Select(i => $"Numpad{i}"))
            .Concat(["Space", "ShiftLeft", "ControlLeft", "AltLeft", "Tab", "Enter", "Backspace", "CapsLock",
                     "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"]);
        Assert.Equal(CustomKeyboardLibrary.MaximumKeys, CustomKeyboardLibrary.Sanitize(everyLetterTwice).Count);
    }

    [Fact]
    public void ACustomSelectionIsRecognisedByShapeAndResolvesToItsOwnAspect()
    {
        var layout = new CustomKeyboardLayout { Name = "Movement", Keys = ["KeyW", "KeyA", "KeyS", "KeyD"] };
        var selection = CustomKeyboardLibrary.Selection(layout);

        Assert.True(KeyboardOverlayCatalog.IsKnown(selection));
        Assert.Same(layout, CustomKeyboardLibrary.Find([layout], selection));

        var definition = KeyboardOverlayCatalog.Resolve(selection, [layout]);
        Assert.Equal(KeyboardOverlayCatalog.Custom, definition.DisplayName);
        Assert.Equal(CustomKeyboardBoard.AspectRatio(CustomKeyboardBoard.Pack(layout.Keys, true)), definition.AspectRatio, 2);

        // A reference whose set is gone must not resolve to someone else's board.
        Assert.Null(CustomKeyboardLibrary.Find([layout], "custom:missing"));
    }

    [Fact]
    public void AClipKeepsDrawingTheKeysItWasRecordedWithAfterTheSetChanges()
    {
        // The whole reason caps are baked rather than referenced.
        var layer = new ClipOverlayLayer("custom:gone", true, ShowMouse: false, Keys:
        [
            new("KeyW", "W", 0),
            new("KeyA", "A", 1), new("KeyS", "S", 1), new("KeyD", "D", 1),
        ]);

        var board = ClipOverlayManifest.BoardOf(layer)!;

        Assert.Equal(2, board.Rows.Count);
        Assert.Equal(["A", "S", "D"], board.Rows[1].Select(cap => cap.Label));
        Assert.False(board.IncludeMouse);
        Assert.Equal(CustomKeyboardBoard.AspectRatio(board), ClipOverlayManifest.AspectOf(layer), 6);
    }

    [Fact]
    public void ClipsRecordedBeforeCapsWereBakedStillResolveThroughTheCatalog()
    {
        var legacy = new ClipOverlayLayer("QWERTY Full", true);

        Assert.Null(ClipOverlayManifest.BoardOf(legacy));
        Assert.Equal(KeyboardOverlayCatalog.Get("QWERTY Full").AspectRatio, ClipOverlayManifest.AspectOf(legacy), 6);
    }

    [Fact]
    public void ArrowClusterSurvivesTheRoundTripThroughBakedCaps()
    {
        // Cluster rows ride on negative row indices rather than a second list.
        var layer = new ClipOverlayLayer("custom:x", true, Keys:
        [
            new("KeyW", "W", 0),
            new("ArrowUp", "↑", -1),
            new("ArrowLeft", "←", -2), new("ArrowDown", "↓", -2), new("ArrowRight", "→", -2),
        ]);

        var board = ClipOverlayManifest.BoardOf(layer)!;

        Assert.Single(board.Rows);
        Assert.Equal(2, board.Cluster.Count);
        Assert.Equal(["↑"], board.Cluster[0].Select(cap => cap.Label));
        Assert.Equal(["←", "↓", "→"], board.Cluster[1].Select(cap => cap.Label));
    }
}
