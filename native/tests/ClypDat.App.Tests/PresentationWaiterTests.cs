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
    public async Task ParkedPlayerAdoptsRetainedPictureAfterGraceInsteadOfTimingOut()
    {
        var presented = false;
        var adopts = 0;
        var stages = new List<PresentationWaiter.Stage>();

        var result = await PresentationWaiter.WaitForSceneAndPresentationAsync(
            _ => Task.FromResult(true),
            () => presented,
            () => true,
            CancellationToken.None,
            stages.Add,
            TimeSpan.FromSeconds(2),
            new PresentationWaiter.ParkedRecovery(
                Decoded: () => 7,
                Stalled: () => true,
                Adopt: () => { adopts++; presented = true; return true; },
                Grace: TimeSpan.FromMilliseconds(20)));

        Assert.True(result);
        Assert.Equal(1, adopts);
        Assert.Equal([PresentationWaiter.Stage.AdoptedRetained], stages);
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
