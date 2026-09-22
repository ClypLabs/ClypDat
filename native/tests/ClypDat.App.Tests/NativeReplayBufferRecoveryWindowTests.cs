using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NativeReplayBufferRecoveryWindowTests
{
    [Fact]
    public void SaveWindow_CompletedRecovery_TrimsUnusableHistory()
    {
        var start = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
        var recoveryEnded = start.AddSeconds(12);

        var canSave = NativeReplayBuffer.TryGetSaveStartAfterRecovery(
            [(start.AddSeconds(4), recoveryEnded)], start, start.AddSeconds(60), out var saveStart);

        Assert.True(canSave);
        Assert.Equal(recoveryEnded, saveStart);
    }

    [Fact]
    public void SaveWindow_ActiveRecovery_RejectsClip()
    {
        var start = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);

        var canSave = NativeReplayBuffer.TryGetSaveStartAfterRecovery(
            [(start.AddSeconds(4), null)], start, start.AddSeconds(60), out _);

        Assert.False(canSave);
    }
}
