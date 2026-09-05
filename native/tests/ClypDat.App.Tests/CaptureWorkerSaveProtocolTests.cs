using System.Text.Json;
using ClypDat.Capture.Abstractions;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CaptureWorkerSaveProtocolTests
{
    [Fact]
    public void AcknowledgementUsesExactLowerCamelNames()
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new CaptureWorkerSaveAcknowledgement(id, "clip.mp4"));
        Assert.Equal($"{{\"saveId\":\"{id}\",\"path\":\"clip.mp4\"}}", json);
    }

    [Fact]
    public void LegacySaveResultWithoutIdentityStillDeserializes()
    {
        var result = JsonSerializer.Deserialize<CaptureWorkerSaveResult>(
            "{\"Path\":\"legacy.mp4\",\"Title\":null,\"CompletedUtc\":\"2026-01-01T00:00:00Z\"}");
        Assert.NotNull(result);
        Assert.Null(result.SaveId);
        Assert.Null(result.RequestedUtc);
    }

    [Fact]
    public void SaveResultCarriesStableIdentityAndRequestTime()
    {
        var id = Guid.NewGuid(); var requested = DateTime.UtcNow;
        var original = new CaptureWorkerSaveResult("clip.mp4", null, requested.AddSeconds(2), null, id, requested);
        var roundTrip = JsonSerializer.Deserialize<CaptureWorkerSaveResult>(JsonSerializer.Serialize(original));
        Assert.Equal(id, roundTrip!.SaveId);
        Assert.Equal(requested, roundTrip.RequestedUtc);
    }

    // Restarting ClypDat while the game is still running leaves the previous
    // worker owning the pipe, so the new instance attaches to it and is handed
    // the backlog. The explicit ack that used to clear it is routinely lost -
    // the redundant second worker exits, recovery starts, and every ack after
    // that is dropped - so the same saves were replayed on every restart, one
    // "Clip Saved" overlay each. Handing the backlog out is the acknowledgement.
    [Fact]
    public void AttachHandsOutEachSaveOnlyOnce()
    {
        var backlog = new List<CaptureWorkerSaveResult>
        {
            new("first.mp4", null, DateTime.UtcNow, SaveId: Guid.NewGuid()),
            new("second.mp4", null, DateTime.UtcNow, SaveId: Guid.NewGuid())
        };

        Assert.Equal(2, CaptureWorkerHost.DrainUnacknowledgedSaves(backlog).Count);
        Assert.Empty(CaptureWorkerHost.DrainUnacknowledgedSaves(backlog));
        Assert.Empty(backlog);
    }

    // A worker outlives any number of app restarts, so an unbounded backlog is
    // a session-long leak. Oldest entries go first: the newest saves are the
    // ones a recovering client still has a reason to hear about.
    [Fact]
    public void BacklogKeepsOnlyTheMostRecentSaves()
    {
        var backlog = new List<CaptureWorkerSaveResult>();
        for (var index = 0; index < CaptureWorkerHost.MaximumUnacknowledgedSaves + 5; index++)
            CaptureWorkerHost.RememberUnacknowledgedSave(backlog, new CaptureWorkerSaveResult($"clip{index}.mp4", null, DateTime.UtcNow));

        Assert.Equal(CaptureWorkerHost.MaximumUnacknowledgedSaves, backlog.Count);
        Assert.Equal("clip5.mp4", backlog[0].Path);
    }

    [Fact]
    public void BacklogAcknowledgesByIdWithLegacyPathFallback()
    {
        var id = Guid.NewGuid();
        var backlog = new List<CaptureWorkerSaveResult>
        {
            new("new.mp4", null, DateTime.UtcNow, SaveId: id),
            new("legacy.mp4", null, DateTime.UtcNow)
        };
        Assert.Equal(1, CaptureWorkerHost.RemoveAcknowledgedSaves(backlog, id, "wrong.mp4"));
        Assert.Equal(1, CaptureWorkerHost.RemoveAcknowledgedSaves(backlog, Guid.NewGuid(), "LEGACY.mp4"));
        Assert.Empty(backlog);
    }
}
