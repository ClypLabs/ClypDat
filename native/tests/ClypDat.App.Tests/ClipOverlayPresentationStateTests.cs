using Avalonia;
using ClypDat.App.Services;
using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipOverlayPresentationStateTests
{
    [Fact]
    public void NewSave_NeverReusesAnOlderSavedSession()
    {
        var sessions = new ClipOverlaySessionManager(Target);
        var first = sessions.BeginSave("save-started");
        Assert.Same(first, sessions.CompleteSave("save-completed"));

        var second = sessions.BeginSave("save-started");

        Assert.NotSame(first, second);
    }

    [Fact]
    public void StandaloneNotification_DoesNotBorrowActiveSaveSession()
    {
        var sessions = new ClipOverlaySessionManager(Target);
        var saving = sessions.BeginSave("save-started");

        Assert.NotSame(saving, sessions.CreateStandalone("game-detected"));
    }

    [Fact]
    public void CompletionDuringEntry_CrossesInsteadOfQueuing()
    {
        // The result of a save used to be held back until the "Clip Saving…"
        // badge had finished arriving, which is what made it land as a snap.
        // With two badge layers it can start crossing straight away.
        var state = new ClipOverlayPresentationState();
        var generation = state.Begin();

        Assert.Equal(ClipOverlayPresentationPhase.Entering, state.Phase);
        Assert.True(state.BeginSwap(generation));
        Assert.Equal(ClipOverlayPresentationPhase.Swapping, state.Phase);
    }

    [Fact]
    public void CompletionAfterEntry_Crosses()
    {
        var state = new ClipOverlayPresentationState();
        var generation = state.Begin();
        Assert.True(state.EnterCompleted(generation));
        Assert.Equal(ClipOverlayPresentationPhase.Dwelling, state.Phase);

        Assert.True(state.BeginSwap(generation));
        Assert.True(state.SwapCompleted(generation));
        Assert.Equal(ClipOverlayPresentationPhase.Dwelling, state.Phase);
    }

    [Fact]
    public void SupersededGeneration_CannotDriveTheCurrentPresentation()
    {
        var state = new ClipOverlayPresentationState();
        var oldGeneration = state.Begin();
        var newGeneration = state.Begin();

        Assert.False(state.EnterCompleted(oldGeneration));
        // The hole this closes: a dwell timer left over from a superseded
        // presentation must not push the current one into its exit.
        Assert.False(state.BeginExit(oldGeneration));
        Assert.False(state.BeginSwap(oldGeneration));

        Assert.True(state.EnterCompleted(newGeneration));
    }

    [Fact]
    public void SwapIsRejectedOnceExitHasStarted()
    {
        var state = new ClipOverlayPresentationState();
        var generation = state.Begin();
        Assert.True(state.EnterCompleted(generation));
        Assert.True(state.BeginExit(generation));

        // Nothing crosses over a badge that is already leaving; the next
        // notification enters fresh once it has parked.
        Assert.False(state.BeginSwap(generation));
        Assert.Equal(ClipOverlayPresentationPhase.Exiting, state.Phase);
    }

    [Fact]
    public void Park_InvalidatesPendingContinuations()
    {
        var state = new ClipOverlayPresentationState();
        var generation = state.Begin();
        state.Idle();

        // Every await in the window resumes into one of these; after a park
        // they must all decline rather than animate a parked window.
        Assert.False(state.EnterCompleted(generation));
        Assert.False(state.BeginSwap(generation));
        Assert.False(state.BeginExit(generation));
        Assert.Equal(ClipOverlayPresentationPhase.Idle, state.Phase);
    }

    private static ClipOverlayTarget Target() => new("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1, ClipOverlayTargetReason.Primary);
}
