namespace ClypDat.App.Services;

/// <summary>Bounded artwork cache owned by one configuration dialog.</summary>
internal sealed class SpotifyPreviewArtCache : IDisposable
{
    private const int Capacity = 16;
    private readonly Dictionary<string, Task<string?>> _images = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _closed = new();
    private readonly SemaphoreSlim _downloads = new(2);
    private bool _disposed;

    // Called by the UI timer. Downloads and filesystem work run outside its frame callback.
    public string? Get(string? url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url)) return null;
        if (!_images.TryGetValue(url, out var image))
        {
            if (_images.Count >= Capacity)
            {
                var oldest = _images.FirstOrDefault(item => item.Value.IsCompleted);
                if (oldest.Key is null) return null;
                _images.Remove(oldest.Key);
                _ = Task.Run(() => DeleteAsync(oldest.Value));
            }
            image = Task.Run(() => LoadAsync(url));
            _images.Add(url, image);
        }
        return image.IsCompletedSuccessfully ? image.Result : null;
    }

    private async Task<string?> LoadAsync(string url)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(16));
        var entered = false;
        try
        {
            await _downloads.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            var path = Path.ChangeExtension(SpotifyOverlayCardRenderer.WorkPath("preview-art"), ".jpg");
            return await SpotifyCoverArtStore.DownloadAsync(url, path, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception) { return null; }
        finally { if (entered) _downloads.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closed.Cancel();
        var images = _images.Values.ToArray();
        _images.Clear();
        _ = Task.Run(async () =>
        {
            await Task.WhenAll(images.Select(DeleteAsync)).ConfigureAwait(false);
            _downloads.Dispose();
            _closed.Dispose();
        });
    }

    private static async Task DeleteAsync(Task<string?> image)
    {
        try
        {
            if (await image.ConfigureAwait(false) is { } path) File.Delete(path);
        }
        catch (Exception) { }
    }
}
