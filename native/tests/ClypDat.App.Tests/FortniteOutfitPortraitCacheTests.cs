using System.Buffers.Binary;
using System.Net;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FortniteOutfitPortraitCacheTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aGo0AAAAASUVORK5CYII=");

    [Theory]
    [InlineData("Character_SlumpAfter", "character_slumpafter")]
    [InlineData("CID_001_Athena_Commando_F_Default", "cid_001_athena_commando_f_default")]
    [InlineData("Character_", null)]
    [InlineData("CID_", null)]
    [InlineData("Character_../../secrets", null)]
    [InlineData("Character_%2fprivate", null)]
    [InlineData("Character_Foo?token=secret", null)]
    [InlineData("https://evil.test/Character_Foo", null)]
    [InlineData("Character_Éclair", null)]
    [InlineData("Skin_Example", null)]
    public void OutfitIdsCannotBecomeArbitraryPathsOrUrls(string id, string? expected)
    {
        Assert.Equal(expected, FortniteOutfitPortraitCache.NormaliseId(id));
        var uri = FortniteOutfitPortraitCache.ImageUri(id);
        if (expected is null) Assert.Null(uri);
        else Assert.Equal($"https://fortnite-api.com/images/cosmetics/br/{expected}/smallicon.png", uri!.AbsoluteUri);
    }

    [Fact]
    public void EncodedBytesAndDecodedDimensionsAreBoundedBeforeBitmapCreation()
    {
        Assert.True(FortniteOutfitPortraitCache.IsBoundedPng(Png));
        var huge = Png.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16, 4), 513);
        Assert.False(FortniteOutfitPortraitCache.IsBoundedPng(huge));
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16, 4), 0);
        Assert.False(FortniteOutfitPortraitCache.IsBoundedPng(huge));
        var tooManyBytes = new byte[FortniteOutfitPortraitCache.MaximumImageBytes + 1];
        Png.CopyTo(tooManyBytes, 0);
        Assert.False(FortniteOutfitPortraitCache.IsBoundedPng(tooManyBytes));
        Assert.False(FortniteOutfitPortraitCache.IsBoundedPng("<html>not an image</html>"u8));
    }

    [Fact]
    public async Task EachMatchOutfitGetsItsOwnOwnedDiskCacheWithoutRepeatedDownloads()
    {
        using var folder = new CacheFixture();
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        }));
        var cache = new FortniteOutfitPortraitCache(folder.Root, http);
        Assert.Equal(Png, await cache.GetBytesAsync("Character_SlumpAfter", default));
        Assert.Equal(Png, await cache.GetBytesAsync("character_slumpafter", default));
        Assert.Equal(Png, await cache.GetBytesAsync("CID_001_Default", default));
        Assert.Equal(2, requests.Count);
        Assert.Equal("/images/cosmetics/br/character_slumpafter/smallicon.png", requests[0].AbsolutePath);
        Assert.Equal("/images/cosmetics/br/cid_001_default/smallicon.png", requests[1].AbsolutePath);
        Assert.Equal(2, Directory.EnumerateFiles(cache.CacheRoot, "*.png").Count());
        Assert.Empty(Directory.EnumerateFiles(folder.Root, "*.tmp", SearchOption.AllDirectories));
        Assert.All(Directory.EnumerateFiles(cache.CacheRoot), path => Assert.True(LibraryPathGuard.IsWithin(folder.Root, path)));
    }

    [Fact]
    public async Task BadIdsAndOfflineFailuresNeverCreatePlaceholderFilesOrRetryLoops()
    {
        using var folder = new CacheFixture();
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            ++calls;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        var cache = new FortniteOutfitPortraitCache(folder.Root, http);
        Assert.Null(await cache.GetBytesAsync("Character_../wrong", default));
        Assert.Equal(0, calls);
        Assert.Null(await cache.GetBytesAsync("Character_Unknown", default));
        Assert.Null(await cache.GetBytesAsync("Character_Unknown", default));
        Assert.Equal(1, calls);
        Assert.Empty(Directory.EnumerateFiles(folder.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellingAnOldOutfitReleasesTheCacheForTheNextClip()
    {
        using var folder = new CacheFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("character_old", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        }));
        var cache = new FortniteOutfitPortraitCache(folder.Root, http);
        using var cancellation = new CancellationTokenSource();
        var old = cache.GetBytesAsync("Character_Old", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal(Png, await cache.GetBytesAsync("Character_New", default));
        Assert.Equal("character_new.png", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(cache.CacheRoot))));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class CacheFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "outfit-cache-tests", Guid.NewGuid().ToString("N"));
        public CacheFixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
