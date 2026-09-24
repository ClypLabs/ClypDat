using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

// Overlay settings reach a capture worker that outlives the app: revisions
// stay ordered across app instances, an attach applies them to a buffer that
// is already recording, and a recording-mode change restarts replay.
public sealed class OverlaySettingsDeliveryTests
{
    [Fact]
    public void RevisionsStayOrderedAcrossAppInstances()
    {
        long earlier = 0;
        var sent = Enumerable.Range(0, 1000).Select(_ => CaptureWorkerProxy.NextOverlayRevision(ref earlier)).ToArray();
        Assert.All(sent.Zip(sent.Skip(1)), pair => Assert.True(pair.Second > pair.First));
        // A relaunched app starts counting again, yet its first change is newer.
        long relaunched = 0;
        Assert.True(CaptureWorkerProxy.NextOverlayRevision(ref relaunched) > sent[^1]);
    }

    [Fact]
    public void AttachAppliesRevisedOverlaysToARunningBuffer()
    {
        var buffer = new Receiver();
        var current = OverlayCaptureSettings.None with { KeyboardLayout = "Full", Revision = 5 };
        // An app that has sent no settings yet leaves the worker's in place.
        Assert.Same(current, CaptureWorkerHost.ApplyAttachOverlays(current, OverlayCaptureSettings.None, buffer));
        Assert.Same(current, CaptureWorkerHost.ApplyAttachOverlays(current, null, buffer));
        Assert.Empty(buffer.Applied);
        var requested = OverlayCaptureSettings.None with { KeyboardLayout = "WASD", Revision = 9 };
        Assert.Same(requested, CaptureWorkerHost.ApplyAttachOverlays(current, requested, buffer));
        Assert.Equal(new[] { requested }, buffer.Applied);
        // Without a buffer the settings wait for the next one.
        Assert.Same(requested, CaptureWorkerHost.ApplyAttachOverlays(current, requested, null));
    }

    [Theory]
    [InlineData(true, false, OverlayRecordingMode.EditableLayers, OverlayRecordingMode.BurnIntoVideo, true)]
    [InlineData(true, false, OverlayRecordingMode.BurnIntoVideo, OverlayRecordingMode.EditableLayers, true)]
    [InlineData(true, false, OverlayRecordingMode.BurnIntoVideo, OverlayRecordingMode.BurnIntoVideo, false)]
    [InlineData(false, false, OverlayRecordingMode.EditableLayers, OverlayRecordingMode.BurnIntoVideo, false)]
    // A start in progress sends its own settings first and adopts that mode.
    [InlineData(true, true, OverlayRecordingMode.EditableLayers, OverlayRecordingMode.BurnIntoVideo, false)]
    [InlineData(true, false, null, OverlayRecordingMode.BurnIntoVideo, false)]
    public void RecordingModeChangeRestartsRunningReplay(bool recording, bool transitioning, string? active, string requested, bool restart) =>
        Assert.Equal(restart, OverlayRecordingModeRestart.Required(recording, transitioning, active, requested));

    private sealed class Receiver : IVideoOverlaySettingsReceiver
    {
        public List<OverlayCaptureSettings> Applied { get; } = new();
        public void SetVideoOverlaySettings(OverlayCaptureSettings settings) => Applied.Add(settings);
    }
}
