using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class RecordingPresentationTests
{
    [Fact]
    public void PausedCapture_UsesSourceNeutralStatusAndKeepsAudioDetail()
    {
        var health = ReplayCaptureHealth.Unknown("Native") with
        {
            State = ReplayCaptureState.Healthy,
            CapturePaused = true
        };

        var presentation = RecordingPresentation.Resolve(health, recording: true, enabled: true);

        Assert.Equal("Recording", presentation.Label);
        Assert.Equal("Video capture paused. Audio continues.", presentation.Detail);
    }
}
