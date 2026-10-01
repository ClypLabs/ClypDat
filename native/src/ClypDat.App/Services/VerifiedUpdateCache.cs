namespace ClypDat.App.Services;

internal sealed class VerifiedUpdateCache(
    Func<AppUpdateInfo, CancellationToken, Task<(string Sha256, long? MaximumBytes)>> verify)
{
    private readonly SemaphoreSlim _verificationGate = new(1, 1);
    private CachedUpdate? _cached;

    public Task<(string Sha256, long? MaximumBytes)> VerifyAsync(AppUpdateInfo update, CancellationToken cancellationToken) =>
        ResolveAsync(update, false, cancellationToken);

    public Task<(string Sha256, long? MaximumBytes)> RefreshAsync(AppUpdateInfo update, CancellationToken cancellationToken) =>
        ResolveAsync(update, true, cancellationToken);

    private async Task<(string Sha256, long? MaximumBytes)> ResolveAsync(
        AppUpdateInfo update, bool refresh, CancellationToken cancellationToken)
    {
        await _verificationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = new UpdateIdentity(update.TagName, update.LatestVersion, update.DownloadUrl,
                update.Sha256, update.ManifestUrl, update.ManifestSignatureUrl, update.VerificationRevision);
            if (refresh) _cached = null;
            if (_cached is { } cached && cached.Identity == identity) return cached.Installer;

            var installer = await verify(update, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _cached = new CachedUpdate(identity, installer);
            return installer;
        }
        finally
        {
            _verificationGate.Release();
        }
    }

    private sealed record UpdateIdentity(
        string TagName,
        Version LatestVersion,
        string DownloadUrl,
        string Sha256,
        string? ManifestUrl,
        string? ManifestSignatureUrl,
        string VerificationRevision);

    private sealed record CachedUpdate(UpdateIdentity Identity, (string Sha256, long? MaximumBytes) Installer);
}
