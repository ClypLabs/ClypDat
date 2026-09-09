using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>Worker-only settings hand-off. Kept off IReplayBuffer so old backends stay valid.</summary>
internal interface IVideoOverlaySettingsReceiver
{
    void SetVideoOverlaySettings(VideoOverlayCaptureSettings settings);
}
