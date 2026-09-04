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
