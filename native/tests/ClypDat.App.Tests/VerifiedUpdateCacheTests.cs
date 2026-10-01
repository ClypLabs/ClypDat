using System.Security.Cryptography;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class VerifiedUpdateCacheTests
{
    private static AppUpdateInfo Update() => new(
        new Version(1, 6, 1),
        new Version(2, 0, 0),
        "v2.0.0",
        "https://github.com/ClypLabs/ClypDat/releases/download/v2.0.0/ClypDat-Setup.exe",
        [],
        [],
        new string('a', 64),
        "https://github.com/ClypLabs/ClypDat/releases/download/v2.0.0/ClypDat-Release.manifest.json",
        "https://github.com/ClypLabs/ClypDat/releases/download/v2.0.0/ClypDat-Release.manifest.sig");

    [Fact]
    public async Task RepeatedChecksVerifyUnchangedReleaseOnlyOnce()
    {
        var verifications = 0;
        var cache = new VerifiedUpdateCache((update, cancellationToken) =>
        {
            verifications++;
            return Task.FromResult<(string Sha256, long? MaximumBytes)>((new string('b', 64), 100));
        });

        var first = await cache.VerifyAsync(Update(), CancellationToken.None);
        var second = await cache.VerifyAsync(Update(), CancellationToken.None);

        Assert.Equal(new string('b', 64), first.Sha256);
        Assert.Equal(100, first.MaximumBytes);
        Assert.Equal(first, second);
        Assert.Equal(1, verifications);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("version")]
    [InlineData("installer-url")]
    [InlineData("installer-digest")]
    [InlineData("manifest-url")]
    [InlineData("signature-url")]
    [InlineData("asset-revision")]
    public async Task ChangedReleaseOrAssetsRequireNewVerification(string changed)
    {
        var verifications = 0;
        var cache = new VerifiedUpdateCache((update, cancellationToken) =>
        {
            verifications++;
            return Task.FromResult<(string Sha256, long? MaximumBytes)>((new string('b', 64), 100));
        });
        var original = Update();
        var replacement = changed switch
        {
            "tag" => original with { TagName = "v2.0.1" },
            "version" => original with { LatestVersion = new Version(2, 0, 1) },
            "installer-url" => original with { DownloadUrl = "https://www.clypdat.xyz/download/ClypDat-Setup.exe" },
            "installer-digest" => original with { Sha256 = new string('c', 64) },
            "manifest-url" => original with { ManifestUrl = "https://www.clypdat.xyz/download/ClypDat-Release.manifest.json" },
            "signature-url" => original with { ManifestSignatureUrl = "https://www.clypdat.xyz/download/ClypDat-Release.manifest.sig" },
            "asset-revision" => original with { VerificationRevision = "replacement asset" },
            _ => throw new ArgumentOutOfRangeException(nameof(changed)),
        };

        await cache.VerifyAsync(original, CancellationToken.None);
        await cache.VerifyAsync(replacement, CancellationToken.None);
        await cache.VerifyAsync(replacement, CancellationToken.None);

        Assert.Equal(2, verifications);
    }

    [Fact]
    public async Task ChangedReleaseNotesDoNotDownloadSignedAssetsAgain()
    {
        var verifications = 0;
        var cache = new VerifiedUpdateCache((update, cancellationToken) =>
        {
            verifications++;
            return Task.FromResult<(string Sha256, long? MaximumBytes)>((new string('b', 64), 100));
        });
        var original = Update();

        await cache.VerifyAsync(original, CancellationToken.None);
        await cache.VerifyAsync(original with { WhatsNew = ["Updated release notes"], Fixes = ["Updated fixes"] }, CancellationToken.None);

        Assert.Equal(1, verifications);
    }

    [Fact]
    public async Task FailedVerificationIsRetried()
    {
        var verifications = 0;
        var cache = new VerifiedUpdateCache((update, cancellationToken) =>
        {
            verifications++;
            if (verifications == 1) throw new CryptographicException("Invalid signature");
            return Task.FromResult<(string Sha256, long? MaximumBytes)>((new string('b', 64), 100));
        });

        await Assert.ThrowsAsync<CryptographicException>(() => cache.VerifyAsync(Update(), CancellationToken.None));
        var result = await cache.VerifyAsync(Update(), CancellationToken.None);

        Assert.Equal(new string('b', 64), result.Sha256);
        Assert.Equal(2, verifications);
    }

    [Fact]
    public async Task FailedPrimaryCandidateDoesNotDiscardVerifiedFallback()
    {
        var verifications = 0;
        var cache = new VerifiedUpdateCache((update, cancellationToken) =>
        {
            verifications++;
            if (update.TagName == "v3.0.0") throw new CryptographicException("Invalid signature");
            return Task.FromResult<(string Sha256, long? MaximumBytes)>((new string('b', 64), 100));
        });
        var fallback = Update();
        var candidates = new[]
        {
            (Info: fallback with { TagName = "v3.0.0", LatestVersion = new Version(3, 0, 0) }, Priority: 0),
            (Info: fallback, Priority: 1),
        };

        var first = await AppUpdateService.SelectFirstVerifiedAsync(candidates,
            (candidate, cancellationToken) => cache.VerifyAsync(candidate.Info, cancellationToken),
            candidate => candidate.Info.TagName, CancellationToken.None);
        var second = await AppUpdateService.SelectFirstVerifiedAsync(candidates,
            (candidate, cancellationToken) => cache.VerifyAsync(candidate.Info, cancellationToken),
            candidate => candidate.Info.TagName, CancellationToken.None);

        Assert.Equal(1, first?.Priority);
        Assert.Equal(1, second?.Priority);
        Assert.Equal(3, verifications);
    }

    [Fact]
    public async Task ConcurrentChecksShareOneVerification()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifications = 0;
        var cache = new VerifiedUpdateCache(async (update, cancellationToken) =>
        {
            verifications++;
            await release.Task.WaitAsync(cancellationToken);
            return (new string('b', 64), 100);
        });

        var first = cache.VerifyAsync(Update(), CancellationToken.None);
        var second = cache.VerifyAsync(Update(), CancellationToken.None);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, verifications);
    }

    [Fact]
    public async Task CanceledWaiterDoesNotCancelAnotherCheck()
    {
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifications = 0;
        var cache = new VerifiedUpdateCache(async (update, cancellationToken) =>
        {
            verifications++;
            await release.Task.WaitAsync(cancellationToken);
            return (new string('b', 64), 100);
        });

        var first = cache.VerifyAsync(Update(), CancellationToken.None);
        var waiter = cache.VerifyAsync(Update(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await cache.VerifyAsync(Update(), CancellationToken.None);

        Assert.Equal(1, verifications);
    }

    [Fact]
    public async Task CanceledVerificationCanBeRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifications = 0;
        var cache = new VerifiedUpdateCache(async (update, cancellationToken) =>
        {
            verifications++;
            await release.Task.WaitAsync(cancellationToken);
            return (new string('b', 64), 100);
        });

        var first = cache.VerifyAsync(Update(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        var result = await cache.VerifyAsync(Update(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new string('b', 64), result.Sha256);
        Assert.Equal(2, verifications);
    }

    [Fact]
    public async Task CanceledCheckCannotReturnCachedVerification()
    {
        using var cancellation = new CancellationTokenSource();
        var verifications = 0;
        var cache = new VerifiedUpdateCache((update, cancellationToken) =>
        {
            verifications++;
            return Task.FromResult<(string Sha256, long? MaximumBytes)>((new string('b', 64), 100));
        });
        await cache.VerifyAsync(Update(), CancellationToken.None);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.VerifyAsync(Update(), cancellation.Token));

        Assert.Equal(1, verifications);
    }

    [Fact]
    public async Task CancellationAfterVerificationDoesNotPopulateCache()
    {
        using var cancellation = new CancellationTokenSource();
        var verifications = 0;
        var cache = new VerifiedUpdateCache((update, cancellationToken) =>
        {
            verifications++;
            if (verifications == 1) cancellation.Cancel();
            return Task.FromResult<(string Sha256, long? MaximumBytes)>((new string('b', 64), 100));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.VerifyAsync(Update(), cancellation.Token));
        await cache.VerifyAsync(Update(), CancellationToken.None);

        Assert.Equal(2, verifications);
    }

    [Fact]
    public async Task InstallationAlwaysFetchesFreshVerification()
    {
        var verifications = 0;
        var cache = new VerifiedUpdateCache((update, cancellationToken) =>
        {
            verifications++;
            return Task.FromResult<(string Sha256, long? MaximumBytes)>((new string(verifications == 1 ? 'b' : 'c', 64), 100));
        });

        await cache.VerifyAsync(Update(), CancellationToken.None);
        var installed = await cache.RefreshAsync(Update(), CancellationToken.None);
        await cache.RefreshAsync(Update(), CancellationToken.None);
        var nextCheck = await cache.VerifyAsync(Update(), CancellationToken.None);

        Assert.Equal(new string('c', 64), installed.Sha256);
        Assert.Equal(installed, nextCheck);
        Assert.Equal(3, verifications);
    }

    [Fact]
    public async Task FailedInstallationVerificationInvalidatesCachedSuccess()
    {
        var verifications = 0;
        var cache = new VerifiedUpdateCache((update, cancellationToken) =>
        {
            verifications++;
            if (verifications == 2) throw new CryptographicException("Invalid signature");
            return Task.FromResult<(string Sha256, long? MaximumBytes)>((new string(verifications == 1 ? 'b' : 'c', 64), 100));
        });

        await cache.VerifyAsync(Update(), CancellationToken.None);
        await Assert.ThrowsAsync<CryptographicException>(() => cache.RefreshAsync(Update(), CancellationToken.None));
        var result = await cache.VerifyAsync(Update(), CancellationToken.None);

        Assert.Equal(new string('c', 64), result.Sha256);
        Assert.Equal(3, verifications);
    }
}
