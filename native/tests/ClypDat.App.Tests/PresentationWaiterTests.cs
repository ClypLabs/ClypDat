using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class PresentationWaiterTests
{
    [Fact]
    public async Task SceneSubmissionConsumesSameBudgetAsNativePresentation()
    {
        var scene = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PresentationWaiter.Stage? timedOut = null;

        var result = await PresentationWaiter.WaitForSceneAndPresentationAsync(
            async _ => { await scene.Task; return true; },
            presented: () => false,
            current: () => true,
            CancellationToken.None,
            stage => timedOut = stage,
            TimeSpan.FromMilliseconds(20));

        Assert.False(result);
        Assert.Equal(PresentationWaiter.Stage.SceneSubmission, timedOut);
    }

    [Fact]
    public async Task NativePresentationUsesRemainingBudgetAfterSceneSubmission()
    {
        PresentationWaiter.Stage? timedOut = null;

        var result = await PresentationWaiter.WaitForSceneAndPresentationAsync(
            async token => { await Task.Delay(5, token); return true; },
            presented: () => false,
            current: () => true,
            CancellationToken.None,
            stage => timedOut = stage,
            TimeSpan.FromMilliseconds(20));

        Assert.False(result);
        Assert.Equal(PresentationWaiter.Stage.NativePresentation, timedOut);
    }

    [Fact]
    public async Task DeclinedSceneFailsImmediatelyWithoutSpendingTheBudget()
    {
        PresentationWaiter.Stage? reported = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await PresentationWaiter.WaitForSceneAndPresentationAsync(
            _ => Task.FromResult(false),
            presented: () => false,
            current: () => true,
            CancellationToken.None,
            stage => reported = stage,
            TimeSpan.FromSeconds(2));

        Assert.False(result);
        Assert.Equal(PresentationWaiter.Stage.SceneDeclined, reported);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500), $"declined scene waited {clock.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task StaleRequestStopsWaitingWithoutAcceptingPresentation()
    {
        var current = true;
        var presented = false;

        var result = await PresentationWaiter.WaitForSceneAndPresentationAsync(
            async token =>
            {
                await Task.Delay(5, token);
                current = false;
                presented = true;
                return true;
            },
            () => presented,
            () => current,
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(1));

        Assert.False(result);
    }
}
