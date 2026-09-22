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
    [Theory]
    [InlineData(false)]
    public void SharedMemoryCodecRoundTripsAllRegions(bool withMask)
    {
        using var map = MemoryMappedFile.CreateNew(null, DetectorFrameCodec.SlotBytes * 3L);
        using var view = map.CreateViewAccessor();
        var timestamp = new DateTime(2026, 9, 5, 1, 2, 3, DateTimeKind.Utc);
        var frame = new DetectorFrameSnapshot(timestamp, Image(10, 7, 1), Image(8, 6, 2), Image(5, 4, 3), withMask ? Image(5, 4, 4) : null);

        DetectorFrameCodec.Write(view, 2, frame);
        var result = DetectorFrameCodec.Read(view, 2);

        Assert.Equal(timestamp, result.CapturedUtc);
        Assert.Equal(frame.First.Pixels, result.First.Pixels);
        Assert.Equal(frame.Second.Pixels, result.Second.Pixels);
        Assert.Equal(frame.Third.Pixels, result.Third.Pixels);
        Assert.Equal(frame.ThirdMask?.Pixels, result.ThirdMask?.Pixels);
    }

    [Fact]
    public void RecycledSlotClearsMaskPresence()
    {
        using var map = MemoryMappedFile.CreateNew(null, DetectorFrameCodec.SlotBytes * 3L);
        using var view = map.CreateViewAccessor();
        var frame = new DetectorFrameSnapshot(DateTime.UtcNow, Image(1, 1, 1), Image(1, 1, 2), Image(5, 4, 3), Image(5, 4, 4));
        DetectorFrameCodec.Write(view, 0, frame);
        DetectorFrameCodec.Write(view, 0, frame with { ThirdMask = null });
        Assert.Null(DetectorFrameCodec.Read(view, 0).ThirdMask);
    }

    [Fact]
    public void CodecRejectsInvalidMaskGeometryOnWriteAndRead()
    {
        using var map = MemoryMappedFile.CreateNew(null, DetectorFrameCodec.SlotBytes * 3L);
        using var view = map.CreateViewAccessor();
        var frame = new DetectorFrameSnapshot(DateTime.UtcNow, Image(1, 1, 1), Image(1, 1, 2), Image(5, 4, 3), Image(4, 5, 4));
        Assert.Throws<InvalidDataException>(() => DetectorFrameCodec.Write(view, 0, frame));
        frame = frame with { ThirdMask = Image(5, 4, 4) };
        DetectorFrameCodec.Write(view, 0, frame);
        const int maskHeader = 16 + 13 + 13 + 12 + 20;
        view.Write(maskHeader, 4); view.Write(maskHeader + 4, 5);
        Assert.Throws<InvalidDataException>(() => DetectorFrameCodec.Read(view, 0));
        view.Write(maskHeader, 0); view.Write(maskHeader + 4, 0); // A partial null marker is invalid.
        Assert.Throws<InvalidDataException>(() => DetectorFrameCodec.Read(view, 0));
        Assert.Throws<InvalidDataException>(() => DetectorFrameCodec.Write(view, -1, frame));
        Assert.Throws<InvalidDataException>(() => DetectorFrameCodec.Read(view, 3));
        Assert.Throws<InvalidDataException>(() => DetectorFrameCodec.Write(view, 0, frame with { First = new(2, 2, [1]) }));
        Assert.Throws<InvalidDataException>(() => DetectorFrameCodec.Write(view, 0, frame with { First = Image(1024, 1024, 1) }));
    }

    [Theory]
    [InlineData(1)]
    public async Task WireRejectsIncompatibleProtocol(int version)
    {
        using var stream = new MemoryStream();
        var bytes = Encoding.UTF8.GetBytes($"{{\"version\":{version},\"type\":\"frame\",\"payload\":{{}}}}");
        stream.Write(BitConverter.GetBytes(bytes.Length)); stream.Write(bytes); stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => DetectorHostWire.ReadAsync(stream, CancellationToken.None));
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
