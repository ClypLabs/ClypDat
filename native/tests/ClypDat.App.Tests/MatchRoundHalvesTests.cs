using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class MatchRoundHalvesTests
{
    [Fact]
    public void RoundHalvesSplitAtSideSwap()
    {
        var match = new MatchStats
        {
            HalftimeAfterRound = 12,
            Rounds = Enumerable.Range(1, 16).Select(number => new MatchRound
            {
                Number = number,
                LocalWon = number % 4 == 0,
                LocalSide = number <= 12 ? "CT" : "T",
                LocalKills = number == 3 ? 4 : 0
            }).ToArray()
        };
        var halves = MatchStatsPresentation.RoundHalves(match);
        Assert.Equal(2, halves.Count);
        Assert.Equal(("First half", "CT", 3, 9, 12), (halves[0].Title, halves[0].Side, halves[0].Won, halves[0].Lost, halves[0].Cells.Count));
        Assert.Equal(("Second half", "T", 1, 3, 4), (halves[1].Title, halves[1].Side, halves[1].Won, halves[1].Lost, halves[1].Cells.Count));
        Assert.Equal(4, halves[0].Cells[2].Kills);
    }
}
