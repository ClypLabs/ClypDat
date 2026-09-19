using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed record RecordingPresentation(string Label, string Detail, string DotColor = "Transparent", bool Flash = false)
{
    internal static RecordingPresentation Resolve(ReplayCaptureHealth health, bool recording, bool enabled, bool stopping = false)
    {
        if (stopping || health.State == ReplayCaptureState.Stopping || health.FullSession.State == FullSessionState.Stopping) return new("Stopping", "Finishing queued recording data.");
        if (!enabled) return new("Off", "Recording is off.");
        if (health.State == ReplayCaptureState.Recovering || health.PipelineRecoveryAction != ReplayPipelineRecoveryAction.None) return new("Recovering", "Reconnecting to the capture worker.");
        if (!recording) return new("Waiting", string.IsNullOrWhiteSpace(health.LastFailure) ? "Waiting for capture." : health.LastFailure);
        if (health.StartupPhase == ReplayCaptureStartupPhase.WaitingForForeground) return new("Waiting", "Waiting for the game to enter the foreground.");
        if (health.State is ReplayCaptureState.Starting or ReplayCaptureState.Unknown or ReplayCaptureState.Stopped || health.StartupPhase == ReplayCaptureStartupPhase.OpeningEncoder || health.FullSession.State == FullSessionState.Starting)
            return new("Starting", "Starting recording.");
        if (health.State == ReplayCaptureState.Failed) return new("Waiting", health.LastFailure);
        var label = health.FullSession.State == FullSessionState.Recording ? "Full Session Recording" : "Recording";
        if (health.CapturePaused) return new(label, "Paused: game is not in the foreground. Audio continues.", "#FFB547");
        return new(label, health.FullSession.State == FullSessionState.Failed ? $"Full Session failed: {health.FullSession.Failure} Replay recording continues." : label, "#F04452", true);
    }
}
