using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DetectorHostTests
{
    [Fact]
    public void SharedMemoryCodecRoundTripsAllRegions()
    {
        using var map = MemoryMappedFile.CreateNew(null, DetectorFrameCodec.SlotBytes * 3L);
        using var view = map.CreateViewAccessor();
        var timestamp = new DateTime(2026, 9, 5, 1, 2, 3, DateTimeKind.Utc);
        var frame = new DetectorFrameSnapshot(timestamp, Image(10, 7, 1), Image(8, 6, 2), Image(5, 4, 3));

        DetectorFrameCodec.Write(view, 2, frame);
        var result = DetectorFrameCodec.Read(view, 2);

        Assert.Equal(timestamp, result.CapturedUtc);
        Assert.Equal(frame.First.Pixels, result.First.Pixels);
        Assert.Equal(frame.Second.Pixels, result.Second.Pixels);
        Assert.Equal(frame.Third.Pixels, result.Third.Pixels);
    }

    // The wire writes with web defaults (camelCase) but payloads used to be read
    // back with JsonElement.Deserialize, which is case-sensitive - so "gameId"
    // never bound to GameId and the host received a policy with no game and no
    // enabled events. It failed silently for as long as the only detector was
    // constructed unconditionally.
    [Fact]
    public async Task PolicyFieldsSurviveTheWireRoundTrip()
    {
        var sent = new DetectorHostPolicy("overwatch", new[] { "team-kill", "play-of-the-game" }, "clypdat.overwatch", "0.1.0", "builtin");
        using var stream = new MemoryStream();

        await DetectorHostWire.WriteAsync(stream, "policy", sent, CancellationToken.None);
        stream.Position = 0;
        var message = await DetectorHostWire.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal("policy", message.Type);
        var received = DetectorHostWire.Deserialize<DetectorHostPolicy>(message.Payload);
        Assert.NotNull(received);
        Assert.Equal("overwatch", received.GameId);
        Assert.Equal(new[] { "team-kill", "play-of-the-game" }, received.EnabledEventIds);
        Assert.Equal("clypdat.overwatch", received.PackId);
    }

    // Same asymmetry in the other direction: a detected event reached the app
    // with every field defaulted, so nothing could ever be clipped.
    [Fact]
    public async Task DetectedEventFieldsSurviveTheWireRoundTrip()
    {
        var sent = new AutoClipDetectorEvent("fortnite", "distance-shot", "Distance Shot (64 M)", "occurrence-1", 0.92, DateTime.UtcNow, 8, 6);
        using var stream = new MemoryStream();

        await DetectorHostWire.WriteAsync(stream, "detected", sent, CancellationToken.None);
        stream.Position = 0;
        var message = await DetectorHostWire.ReadAsync(stream, CancellationToken.None);

        var received = DetectorHostWire.Deserialize<AutoClipDetectorEvent>(message!.Payload);
        Assert.NotNull(received);
        Assert.Equal("fortnite", received.GameId);
        Assert.Equal("distance-shot", received.EventId);
        Assert.Equal("Distance Shot (64 M)", received.EventLabel);
        Assert.Equal(8, received.LeadSeconds);
    }

    [Fact]
    public void CrashCircuitBreaksOnThirdCrashWithinTenMinutes()
    {
        var breaker = new DetectorCrashCircuitBreaker();
        var now = DateTime.UtcNow;
        Assert.Equal(1, breaker.Record(now));
        Assert.Equal(2, breaker.Record(now.AddMinutes(4)));
        Assert.Equal(3, breaker.Record(now.AddMinutes(9)));
        Assert.Equal(1, breaker.Record(now.AddMinutes(20)));
    }

    [Fact]
    public void HostResolverUsesDedicatedSiblingWhenPresent()
    {
        var app = Path.Combine("C:\\ClypDat", "ClypDatRecorder.exe");
        var result = DetectorHostExecutable.Resolve(app, path => path.EndsWith(DetectorHostExecutable.FileName));
        Assert.Equal(Path.Combine("C:\\ClypDat", DetectorHostExecutable.FileName), result);
    }

    [Fact]
    public void PackArchiveRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-pack-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "pack.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("../escape.json").Open())) writer.Write("{}");
            var file = new AutoClipPackFile("../escape.json", 2, Convert.ToHexString(SHA256.HashData("{}"u8.ToArray())).ToLowerInvariant());
            Assert.Throws<InvalidDataException>(() => AutoClipPackStore.VerifyAndExtractArchive(archivePath, Path.Combine(root, "stage"), [file]));
            Assert.False(File.Exists(Path.Combine(root, "escape.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PackArchiveRejectsWrongFileHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-pack-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "pack.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("graph.json").Open())) writer.Write("{}");
            var file = new AutoClipPackFile("graph.json", 2, new string('0', 64));
            Assert.Throws<InvalidDataException>(() => AutoClipPackStore.VerifyAndExtractArchive(archivePath, Path.Combine(root, "stage"), [file]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DetachedSignatureRejectsUnauthenticatedPackManifest()
    {
        var signature = Encoding.UTF8.GetBytes(Convert.ToBase64String(new byte[384]));
        Assert.ThrowsAny<CryptographicException>(() => ReleaseSigning.VerifyDetached("{}"u8, signature, "Detector pack manifest"));
    }

    private static GrayDetectorImage Image(int width, int height, byte seed) =>
        new(width, height, Enumerable.Range(0, width * height).Select(value => unchecked((byte)(value + seed))).ToArray());
}
