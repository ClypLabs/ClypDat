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
            _ => scene.Task,
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
            async token => await Task.Delay(5, token),
            presented: () => false,
            current: () => true,
            CancellationToken.None,
            stage => timedOut = stage,
            TimeSpan.FromMilliseconds(20));

        Assert.False(result);
        Assert.Equal(PresentationWaiter.Stage.NativePresentation, timedOut);
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
            },
            () => presented,
            () => current,
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(1));

        Assert.False(result);
    }
}
