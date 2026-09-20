namespace ClypDat.App.Services;

/// <summary>
/// Bounds a write to a peer that may have stopped reading.
/// </summary>
/// <remarks>
/// A named pipe write completes only once the peer drains the buffer. A client
/// that dies mid-message - a UI being restarted by a publish, say - leaves the
/// write pending forever, and because every message shares one write gate, the
/// whole channel wedges behind it: the process keeps working while every reply
/// it owes anyone queues behind a write that can never finish.
/// </remarks>
internal static class PipeWriteGuard
{
    /// <summary>
    /// Runs <paramref name="write"/> under <paramref name="gate"/>, giving both
    /// the gate and the write <paramref name="timeout"/> to complete. A peer
    /// that overruns it is reported through <paramref name="onStalled"/> - it is
    /// not coming back, and the caller is expected to drop it.
    /// </summary>
    /// <exception cref="IOException">The peer stalled. The gate is released.</exception>
    public static async Task WriteAsync(
        SemaphoreSlim gate,
        TimeSpan timeout,
        Func<CancellationToken, Task> write,
        Action onStalled,
        CancellationToken cancellationToken)
    {
        // A gate this call cannot take within the budget is being held by another
        // write to the same stalled peer. That one is bounded too and will let go,
        // so the channel recovers on its own - but this message is already late,
        // and waiting behind it is how the wedge used to spread.
        if (!await gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            onStalled();
            throw new IOException("Pipe write gate was held past its budget by a stalled peer.");
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            try
            {
                await write(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                onStalled();
                throw new IOException("Pipe peer stopped reading before the message could be written.");
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
