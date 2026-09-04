using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LibraryMotionGateTests
{
    [Fact]
    public void RepeatedMotion_RejectsOldTimerAndResumesOnceAfterLatestSettle()
    {
        var gate = new LibraryMotionGate();
        var oldGeneration = gate.Begin();
        var currentGeneration = gate.Begin();

        Assert.False(gate.TrySettle(oldGeneration, candidateCanResume: true));
        Assert.True(gate.IsActive);
        Assert.True(gate.TrySettle(currentGeneration, candidateCanResume: true));
        Assert.False(gate.TrySettle(currentGeneration, candidateCanResume: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settle_OnlyResumesValidCurrentCandidate(bool candidateCanResume)
    {
        var gate = new LibraryMotionGate();
        var generation = gate.Begin();

        Assert.Equal(candidateCanResume, gate.TrySettle(generation, candidateCanResume));
        Assert.False(gate.IsActive);
    }
}
