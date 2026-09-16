using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class KeyboardOverlayGeometryTests
{
    [Theory]
    [InlineData(KeyboardOverlayCatalog.QwertyCompact)]
    [InlineData(KeyboardOverlayCatalog.AzertyCompact)]
    public void CompactBoardsCarryEscapeAndTheLeftModifiers(string layout)
    {
        var codes = KeyboardOverlayGeometry.Describe(layout).Rows
            .SelectMany(row => row.Keys)
            .Select(key => key.Code)
            .ToArray();

        Assert.Contains("Escape", codes);
        Assert.Contains("ShiftLeft", codes);
        Assert.Contains("AltLeft", codes);
        Assert.Contains("ControlLeft", codes);
    }

    // Esc and the left Shift both take space ahead of the letters, so the row
    // offsets exist to put Q, A and Z back where they were relative to each
    // other. A board whose home row no longer sits half a key right of the top
    // one stops reading as a keyboard.
    [Fact]
    public void AddedModifiersKeepTheLettersStaggered()
    {
        var rows = KeyboardOverlayGeometry.Describe(KeyboardOverlayCatalog.QwertyCompact).Rows;

        var q = StartOf(rows[0], "Q");
        Assert.Equal(.42, StartOf(rows[1], "A") - q, 3);
        Assert.Equal(.72, StartOf(rows[2], "Z") - q, 3);
    }

    private static double StartOf(KeyboardOverlayGeometry.Row row, string label)
    {
        var start = row.Offset;
        foreach (var key in row.Keys)
        {
            if (key.Label == label) return start;
            start += key.Units + KeyboardOverlayGeometry.Gap;
        }

        throw new Xunit.Sdk.XunitException($"Row has no '{label}' cap.");
    }
}
