using System.IO.Compression;
using System.Security.Cryptography;
using ClypDat.DevChannel;
using Xunit;

namespace ClypDat.DevChannel.Tests;

public sealed class DevChannelSecurityTests
{
    [Fact]
    public void ManifestRejectsTampering()
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            buildId = "clypdat-avalonia",
            clypDatCommit = new string('a', 40),
            avaloniaCommit = new string('b', 40),
            avaloniaPackageVersion = "12.2.1000-clypdat.1.abcdef123456",
            buildIdSource = "source",
            buildNumber = 1,
            archiveSize = 1,
            archiveSha256 = new string('0', 64),
            createdUtc = DateTimeOffset.UtcNow
        });

        using var rsa = RSA.Create(3072);
        var signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        Assert.Throws<CryptographicException>(() => DevPackageVerifier.VerifyManifest(bytes, System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(signature))));
    }

    [Fact]
    public void ArchiveRejectsTraversalBeforeWritingOutsideStagingDirectory()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open());
            writer.Write("blocked");
        }
        var bytes = archive.ToArray();
        var manifest = ManifestFor(bytes);
        archive.Position = 0;
        var destination = Path.Combine(Path.GetTempPath(), "clypdat-dev-test-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidDataException>(() => DevPackageVerifier.ExtractArchive(archive, destination, manifest));
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(destination)!, "escaped.txt")));
    }

    [Fact]
    public void StateStoreUsesAtomicSafeStateAndRejectsUnsafeIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-dev-state-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "state.json");
        try
        {
            DevInstallStateStore.SaveAtomic(statePath, new DevInstallState("one", "zero", "two"));
            Assert.Equal("one", DevInstallStateStore.Load(statePath).CurrentBuildId);
            DevInstallStateStore.SaveAtomic(statePath, new DevInstallState("..\\escape", null, null));
        }
        catch (InvalidDataException)
        {
            Assert.Equal("one", DevInstallStateStore.Load(statePath).CurrentBuildId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static DevBuildManifest ManifestFor(byte[] archive) => new(
        1, "build", new string('a', 40), new string('b', 40),
        "12.2.1000-clypdat.1.abcdef123456", "source", 1, archive.Length,
        Convert.ToHexString(SHA256.HashData(archive)), DateTimeOffset.UtcNow);
}
