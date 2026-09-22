using System.Security.Cryptography;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SecurityUpdateTests
{
    [Fact]
    public async Task UnverifiedNewerReleaseFallsBackToVerifiedCandidate()
    {
        var attempted = new List<int>();
        var chosen = await AppUpdateService.SelectFirstVerifiedAsync(new[] { 99, 2, 1 }, (version, _) =>
        {
            attempted.Add(version);
            return version == 99 ? Task.FromException(new CryptographicException("Invalid signature")) : Task.CompletedTask;
        }, version => version.ToString(), CancellationToken.None);
        Assert.Equal(2, chosen);
        Assert.Equal(new[] { 99, 2 }, attempted);
    }

    [Fact]
    public async Task NoVerifiedCandidateMeansNoUpdate()
    {
        var chosen = await AppUpdateService.SelectFirstVerifiedAsync(new[] { 99, 2 },
            (_, _) => Task.FromException(new InvalidDataException("Missing signed manifest")),
            version => version.ToString(), CancellationToken.None);
        Assert.Null(chosen);
    }

    [Fact]
    public async Task SourceTimeoutStillAllowsAnotherCandidate()
    {
        var chosen = await AppUpdateService.SelectFirstVerifiedAsync(new[] { 2, 1 },
            (version, _) => version == 2 ? Task.FromException(new TaskCanceledException("Source timed out")) : Task.CompletedTask,
            version => version.ToString(), CancellationToken.None);
        Assert.Equal(1, chosen);
    }

    [Fact]
    public async Task UserCancellationDoesNotProbeAnotherCandidate()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppUpdateService.SelectFirstVerifiedAsync(new[] { 2, 1 },
            (_, token) => { attempts++; cancellation.Cancel(); return Task.FromCanceled(token); },
            version => version.ToString(), cancellation.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AlreadyCanceledCheckDoesNotStartVerification()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppUpdateService.SelectFirstVerifiedAsync(new[] { 1 },
            (_, _) => throw new Xunit.Sdk.XunitException("Verification started after cancellation"),
            version => version.ToString(), cancellation.Token));
    }

    private static ReleaseManifestAsset Installer(long size) => new()
    {
        Name = "ClypDat-Setup.exe", Size = size, Sha256 = new string('A', 64)
    };

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483649)]
    public void InvalidSignedSizeCannotDisableDownloadLimit(long size) =>
        Assert.Throws<InvalidDataException>(() => AppUpdateService.SignedInstaller(new ReleaseManifest { Assets = new[] { Installer(size) } }));

    [Fact]
    public void AmbiguousSignedInstallerIsRejected() =>
        Assert.Throws<InvalidDataException>(() => AppUpdateService.SignedInstaller(new ReleaseManifest { Assets = new[] { Installer(10), Installer(20) } }));

    [Fact]
    public void SignedInstallerReturnsDigestAndExactSize()
    {
        var verified = AppUpdateService.SignedInstaller(new ReleaseManifest { Assets = new[] { Installer(10) } });
        Assert.Equal(new string('a', 64), verified.Sha256);
        Assert.Equal(10, verified.MaximumBytes);
    }

    [Fact]
    public async Task ChunkedOversizeInstallerIsRejectedBeforeWritingExcessBytes()
    {
        using var source = new MemoryStream(new byte[11]);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateService.CopyInstallerAsync(source, destination, 10, null, null, CancellationToken.None));
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task TruncatedSignedInstallerIsRejected()
    {
        using var source = new MemoryStream(new byte[9]);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateService.CopyInstallerAsync(source, destination, 10, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task UnsignedDownloadsStillHaveAHardLimit()
    {
        using var source = new MemoryStream(new byte[1]);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateService.CopyInstallerAsync(source, destination, null,
            AppUpdateService.MaximumInstallerBytes + 1, null, CancellationToken.None));
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public async Task ExactSignedDownloadIsCopied()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        using var source = new MemoryStream(bytes);
        using var destination = new MemoryStream();
        await AppUpdateService.CopyInstallerAsync(source, destination, bytes.Length, null, null, CancellationToken.None);
        Assert.Equal(bytes, destination.ToArray());
    }
}
