using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipInfoCaptureDetailsTests
{
    [Theory]
    [InlineData("Double Elimination")]
    [InlineData(null)]
    public void SaveCompletionPreservesRecordedLayersAndOtherMetadata(string? eventType)
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-capture-details-" + Guid.NewGuid());
        var clip = Path.Combine(root, "Clips", "Fortnite", "clip.mp4");
        try
        {
            var manifest = new ClipOverlayManifest(ClipOverlayManifest.CurrentVersion,
                new ClipOverlayLayer("Facecam", true),
                new ClipOverlayLayer("QWERTY Compact", true));
            var capturedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
            ClipInfoSidecar.Save(root, clip, new ClipInfo("Fortnite", null,
                CapturedAt: capturedAt, SpotifyTrack: "Track", OverlayManifest: manifest));

            ClipInfoSidecar.SaveCaptureDetails(root, clip, new ClipInfo("Fortnite", eventType,
                FileTitle: eventType ?? "Fortnite", CapturedAt: DateTimeOffset.UtcNow,
                CaptureSource: "Game", AutoClipMarkers: [new("double-elimination", "Double Elimination", 22)]));

            var saved = Assert.IsType<ClipInfo>(ClipInfoSidecar.Load(root, clip));
            Assert.Equal(manifest, saved.OverlayManifest);
            Assert.Equal(capturedAt, saved.CapturedAt);
            Assert.Equal("Track", saved.SpotifyTrack);
            Assert.Equal(eventType, saved.AutoClipEventType);
            Assert.Equal(eventType ?? "Fortnite", saved.FileTitle);
            Assert.Equal("Game", saved.CaptureSource);
            Assert.Equal(22, Assert.Single(saved.AutoClipMarkers!).OffsetSeconds);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SaveCompletionCreatesMetadataWhenWorkerDidNotWriteIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-capture-details-" + Guid.NewGuid());
        var clip = Path.Combine(root, "Clips", "clip.mp4");
        try
        {
            var details = new ClipInfo("Fortnite", "Double Elimination", CaptureSource: "Game");
            ClipInfoSidecar.SaveCaptureDetails(root, clip, details);
            Assert.Equal(details, ClipInfoSidecar.Load(root, clip));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SharedLinkIsRememberedAndForgottenWithoutTouchingTheRest()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-capture-details-" + Guid.NewGuid());
        var clip = Path.Combine(root, "Clips", "clip.mp4");
        try
        {
            var details = new ClipInfo("Fortnite", null, CustomTitle: "Clutch");
            ClipInfoSidecar.Save(root, clip, details);
            ClipInfoSidecar.SaveSharedLink(root, clip, "i2KExOXpKOkYq6i1OA3n7g", "https://www.clypdat.xyz/c/i2KExOXpKOkYq6i1OA3n7g");
            var shared = ClipInfoSidecar.Load(root, clip)!;
            Assert.Equal(details with { SharedClipId = "i2KExOXpKOkYq6i1OA3n7g", SharedClipUrl = "https://www.clypdat.xyz/c/i2KExOXpKOkYq6i1OA3n7g" }, shared);

            ClipInfoSidecar.SaveSharedLink(root, clip, null, null);
            Assert.Equal(details, ClipInfoSidecar.Load(root, clip));

            // A clip with no details yet gets a file holding just the link.
            var bare = Path.Combine(root, "Clips", "bare.mp4");
            ClipInfoSidecar.SaveSharedLink(root, bare, "AAAAAAAAAAAAAAAAAAAAAA", "https://www.clypdat.xyz/c/AAAAAAAAAAAAAAAAAAAAAA");
            Assert.Equal("https://www.clypdat.xyz/c/AAAAAAAAAAAAAAAAAAAAAA", ClipInfoSidecar.Load(root, bare)?.SharedClipUrl);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
