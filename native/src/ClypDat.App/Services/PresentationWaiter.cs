using System.Diagnostics;

namespace ClypDat.App.Services;

// Native presentation status advances independently of VLC's coarse clock events.
internal static class PresentationWaiter
{
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
