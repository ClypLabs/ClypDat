using System.Collections.Concurrent;
using System.Net.Http;

namespace ClypDat.App.Services;

/// <summary>
/// One quiet line per outage instead of one stack trace per retry. Being
/// offline is ordinary - DNS dropping, a laptop between networks, Windows still
/// bringing the network up - and a tester's log dump carried 268 notice-board
/// ERRORs from a single half-hour outage, burying the errors that mattered.
/// Anything that is not a reachability failure is left for the caller to log.
/// </summary>
internal static class NetworkFailureLog
{
    private sealed class Streak
    {
        public readonly DateTime Since = DateTime.UtcNow;
        public int Failures;
    }

    private static readonly ConcurrentDictionary<string, Streak> Streaks = new(StringComparer.Ordinal);

    // No status code means the request never got an answer: name resolution,
    // refused or reset connections, TLS. A timeout is the same story slower.
    internal static bool IsUnreachable(Exception error) => error switch
    {
        HttpRequestException { StatusCode: null } => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        _ => false,
    };

    /// <summary>Records a reachability failure for <paramref name="source"/>,
    /// logging only the first of a streak. False when the error is something
    /// else, which the caller still owns.</summary>
    internal static bool Failed(string source, Exception error)
    {
        if (!IsUnreachable(error)) return false;
        var streak = Streaks.GetOrAdd(source, _ => new Streak());
        if (Interlocked.Increment(ref streak.Failures) == 1)
            AppLog.Info($"{source}: unreachable ({error.GetBaseException().Message}); retrying quietly.");
        return true;
    }

    internal static void Succeeded(string source)
    {
        if (Streaks.TryRemove(source, out var streak))
            AppLog.Info($"{source}: reachable again after {(DateTime.UtcNow - streak.Since).TotalSeconds:0}s, {streak.Failures} failed attempt(s).");
    }

    internal static int Failures(string source) => Streaks.TryGetValue(source, out var streak) ? Volatile.Read(ref streak.Failures) : 0;
}
