using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal static class ReplayAutoStart
{
    // Whether a detection tick or settings change should start replay. A
    // buffer suspended for an unavailable display or session is still armed
    // (IsRecording), so it is never started again; the worker resumes it.
    internal static bool ShouldStart(bool shouldRecord, IReplayBuffer? buffer, bool transitioning) =>
        shouldRecord && buffer is { IsRecording: false } && !transitioning;
}
