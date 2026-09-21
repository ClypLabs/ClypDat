using System.Diagnostics;

namespace ClypDat.App.Services;

// Native presentation status advances independently of VLC's coarse clock events.
internal static class PresentationWaiter
{
    internal enum Stage { SceneSubmission, NativePresentation, SceneDeclined }

    internal static async Task<bool> WaitForSceneAndPresentationAsync(
        Func<CancellationToken, Task<bool>> submitScene, Func<bool> presented, Func<bool> current,
        CancellationToken token, Action<Stage>? timedOut = null, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(limit);
        var clock = Stopwatch.StartNew();
        bool submitted;
        try
        {
            submitted = await submitScene(budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !token.IsCancellationRequested)
        {
            timedOut?.Invoke(Stage.SceneSubmission);
            return false;
        }
        if (!current()) return false;
        // Nothing reached the compositor, so its revision is still the seek
        // barrier's zero and it will refuse to compose. Waiting out the budget
        // for a presentation that cannot happen only delays the next seek.
        if (!submitted)
        {
            timedOut?.Invoke(Stage.SceneDeclined);
            return false;
        }

        var remaining = limit - clock.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            timedOut?.Invoke(Stage.SceneSubmission);
            return false;
        }
        try
        {
            var result = await WaitAsync(presented, current, budget.Token, remaining).ConfigureAwait(false);
            if (!result && !token.IsCancellationRequested && current() && !presented())
                timedOut?.Invoke(Stage.NativePresentation);
            return result;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !token.IsCancellationRequested)
        {
            timedOut?.Invoke(Stage.NativePresentation);
            return false;
        }
    }

    internal static async Task<bool> WaitAsync(Func<bool> presented, Func<bool> current,
        CancellationToken token, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        while (clock.Elapsed < limit)
        {
            token.ThrowIfCancellationRequested();
            if (!current()) return false;
            if (presented()) return true;
            await Task.Delay(4, token).ConfigureAwait(false);
        }
        return current() && presented();
    }
}
