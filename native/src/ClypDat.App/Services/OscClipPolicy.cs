using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Immutable capture state published by the UI for the UDP receive thread.
internal sealed record OscCaptureSnapshot(IReplayBuffer Buffer, ReplayBufferConfig Config,
    long Generation, long WorkerGeneration, string GameName, string? Refusal);

internal sealed record OscClipRequest(OscCaptureSnapshot Capture, DateTime ReceivedUtc, CancellationToken ListenerLifetime)
{
    public ReplayClipWindow Window => new(ReceivedUtc.AddSeconds(-Capture.Config.DurationSeconds), ReceivedUtc);
}

internal static class OscClipPolicy
{
    public static string? Refusal(bool quitting, bool busy, bool reconfiguring, bool arming, bool suspended, bool available) =>
        quitting ? "application is quitting" :
        busy ? "another operation is busy" :
        reconfiguring ? "capture is reconfiguring" :
        arming ? "capture is arming" :
        suspended ? "capture is suspended" :
        !available ? "capture is unavailable" : null;

    public static string? Validate(OscClipRequest request, OscCaptureSnapshot? current) =>
        request.ListenerLifetime.IsCancellationRequested ? "OSC listener changed or stopped" :
        request.Capture.Refusal is { } refusal ? refusal :
        current is null ? "capture is unavailable" :
        !ReferenceEquals(request.Capture.Buffer, current.Buffer) || request.Capture.Generation != current.Generation ||
        request.Capture.WorkerGeneration != current.WorkerGeneration ||
        !ReferenceEquals(request.Capture.Config, current.Config) ? "capture changed since reception" : current.Refusal;
}
