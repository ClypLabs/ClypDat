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
    public void CompletionBeforeEntry_QueuesSavedUntilSavingEntryFinishes()
    {
        var state = new ClipOverlayPresentationState();
        var saving = Request("Clip Saving…");
        var saved = saving with { Text = "Clip Saved", PlaySound = true };
        var generation = state.Begin();

        Assert.Equal(ClipOverlayUpdateDisposition.Queue, state.Update(generation, saved));
        Assert.True(state.BeginEntry(generation));
        Assert.Equal(ClipOverlayPresentationPhase.Entering, state.Phase);

        Assert.Same(saved, state.CompleteEntry(generation));
        Assert.Equal(ClipOverlayPresentationPhase.Dwelling, state.Phase);
    }

    [Fact]
    public void SupersededFrameCallback_CannotStartNewSessionEntry()
    {
        var state = new ClipOverlayPresentationState();
        var oldGeneration = state.Begin();
        var newGeneration = state.Begin();

        Assert.False(state.BeginEntry(oldGeneration));
        Assert.True(state.BeginEntry(newGeneration));
    }

    [Fact]
    public void CompletionAfterEntry_AppliesImmediatelyAndDoesNotQueue()
    {
        var state = new ClipOverlayPresentationState();
        var request = Request("Clip Saved");
        var generation = state.Begin();
        Assert.True(state.BeginEntry(generation));
        Assert.Null(state.CompleteEntry(generation));

        Assert.Equal(ClipOverlayUpdateDisposition.Apply, state.Update(generation, request));
    }

    [Fact]
    public void CompletionDuringExit_RestartsInsteadOfBeingDropped()
    {
        var state = new ClipOverlayPresentationState();
        var generation = state.Begin();
        Assert.True(state.BeginEntry(generation));
        Assert.Null(state.CompleteEntry(generation));
        Assert.True(state.BeginExit());

        Assert.Equal(ClipOverlayUpdateDisposition.Restart, state.Update(generation, Request("Clip Saved")));
    }

    [Fact]
    public void Hide_InvalidatesPendingFrameCallbacks()
    {
        var state = new ClipOverlayPresentationState();
        var generation = state.Begin();
        state.Hide();

        Assert.False(state.BeginEntry(generation));
        Assert.Equal(ClipOverlayPresentationPhase.Hidden, state.Phase);
    }

    private static ClipOverlayRequest Request(string text)
    {
        return new ClipOverlayRequest(new ClipOverlaySession("test", Target()), text, ClipOverlaySide.Right, false, true);
    }

    private static ClipOverlayTarget Target() => new("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1, ClipOverlayTargetReason.Primary);
}
