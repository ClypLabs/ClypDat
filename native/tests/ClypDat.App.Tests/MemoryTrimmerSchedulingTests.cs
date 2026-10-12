using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class MemoryTrimmerSchedulingTests
{
    [Fact]
    public void DeferredTrimDoesNotPostponeCompactionAfterEditorCloses()
    {
        var lastCompactingTrim = new DateTime(2026, 10, 13, 1, 8, 37, DateTimeKind.Utc);
        var lastDeferredTrim = lastCompactingTrim.AddMinutes(4);
        var editorClosedAndSettled = lastDeferredTrim.AddSeconds(20);
        var (_, deferred) = MemoryTrimmer.ResolveTrimMode(editorOpen: false, gameRunning: false, lowLevelInputHook: false);

        Assert.True(editorClosedAndSettled >=
            MemoryTrimmer.GetNextEligibleTrimUtc(deferred, lastDeferredTrim, lastCompactingTrim));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void DeferredAttemptsRemainThrottledDuringPlaybackOrInputHooks(bool editorOpen, bool gameRunning, bool inputHook)
    {
        var lastCompactingTrim = new DateTime(2026, 10, 13, 1, 8, 37, DateTimeKind.Utc);
        var lastDeferredTrim = lastCompactingTrim.AddMinutes(4);
        var (_, deferred) = MemoryTrimmer.ResolveTrimMode(editorOpen, gameRunning, inputHook);

        Assert.Equal(lastDeferredTrim.AddMinutes(3),
            MemoryTrimmer.GetNextEligibleTrimUtc(deferred, lastDeferredTrim, lastCompactingTrim));
    }

    [Fact]
    public void RecentCompactionStillThrottlesTheNextEditorCloseCleanup()
    {
        var lastCompactingTrim = new DateTime(2026, 10, 13, 1, 12, 33, DateTimeKind.Utc);
        var lastDeferredTrim = lastCompactingTrim.AddSeconds(40);
        var (_, deferred) = MemoryTrimmer.ResolveTrimMode(editorOpen: false, gameRunning: false, lowLevelInputHook: false);

        Assert.Equal(lastCompactingTrim.AddMinutes(3),
            MemoryTrimmer.GetNextEligibleTrimUtc(deferred, lastDeferredTrim, lastCompactingTrim));
    }
}
