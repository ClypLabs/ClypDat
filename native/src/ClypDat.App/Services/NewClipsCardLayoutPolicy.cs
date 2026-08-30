namespace ClypDat.App.Services;

// The popup deliberately has a fixed two-card grid, independent of the
// available viewport width. Keeping this policy free of Avalonia makes its
// centring contract deterministic and easy to verify.
internal static class NewClipsCardLayoutPolicy
{
    public const int CardsPerRow = 2;

    public static IReadOnlyList<int> CreateRowLengths(int clipCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clipCount);

        var rows = new List<int>((clipCount + CardsPerRow - 1) / CardsPerRow);
        while (clipCount > 0)
        {
            var rowLength = Math.Min(CardsPerRow, clipCount);
            rows.Add(rowLength);
            clipCount -= rowLength;
        }

        return rows;
    }
}
