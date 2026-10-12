using Avalonia;
using Avalonia.Media.Imaging;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LibraryThumbnailRetentionTests
{
    [Fact]
    public void SettledHiddenViewport_ReleasesCardReferenceAndResumesFromCache()
    {
        AvaloniaTestThread.Run(() =>
        {
            var path = $"settled-retention-{Guid.NewGuid()}.jpg";
            ClipCardViewModel.SetPreviewDecodeWidth(256, 1);
            using var image = new WriteableBitmap(new PixelSize(256, 144), new Vector(96, 96));
            var media = new MediaFileInfo("clip", "C:\\test\\clip.mp4", DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(5), 1, path, [], 640, 360, 60);
            var card = new ClipCardViewModel(media, "C:\\test");
            try
            {
                CardThumbnailCache.Store(path, 256, image);
                card.SetPreviewLifecycle(true, true);
                card.SetPreviewLifecycle(true, false);
                Assert.Same(image, card.PreviewImage); // quick navigation stays warm

                card.ReleaseInactivePreviewImage();
                Assert.Null(card.PreviewImage);
                Assert.Same(image, CardThumbnailCache.Get(path, 256));
                card.SetPreviewLifecycle(true, true);
                Assert.Same(image, card.PreviewImage); // no file exists; resume must be synchronous

                card.ReleaseInactivePreviewImage();
                Assert.Same(image, card.PreviewImage); // a settle callback cannot blank an active card
            }
            finally { CardThumbnailCache.Invalidate(path); }
        }, TimeSpan.FromSeconds(10), "Settled thumbnail cache resume timed out.");
    }

    [Fact]
    public void SettledHiddenViewport_DropsImageAfterCacheEviction()
    {
        AvaloniaTestThread.Run(() =>
        {
            var path = $"evicted-retention-{Guid.NewGuid()}.jpg";
            ClipCardViewModel.SetPreviewDecodeWidth(256, 1);
            using var image = new WriteableBitmap(new PixelSize(256, 144), new Vector(96, 96));
            var media = new MediaFileInfo("clip", "C:\\test\\clip.mp4", DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(5), 1, path, [], 640, 360, 60);
            var card = new ClipCardViewModel(media, "C:\\test");
            try
            {
                CardThumbnailCache.Store(path, 256, image);
                card.SetPreviewLifecycle(true, true);
                card.SetPreviewLifecycle(true, false);
                CardThumbnailCache.Clear();
                Assert.Same(image, card.PreviewImage); // eviction cannot disrupt a brief navigation

                card.ReleaseInactivePreviewImage();
                Assert.Null(card.PreviewImage);
                Assert.Null(CardThumbnailCache.Get(path, 256));
                Assert.Equal(new PixelSize(256, 144), image.PixelSize); // borrowers retain ownership safely
            }
            finally { CardThumbnailCache.Invalidate(path); }
        }, TimeSpan.FromSeconds(10), "Settled thumbnail eviction timed out.");
    }

    [Fact]
    public void HiddenViewport_SurvivesEvictionAndRefreshesAtomically()
    {
        AvaloniaTestThread.Run(() =>
        {
            var path = $"retention-{Guid.NewGuid()}.jpg";
            ClipCardViewModel.SetPreviewDecodeWidth(256, 1);
            using var first = new WriteableBitmap(new PixelSize(256, 144), new Vector(96, 96));
            using var replacement = new WriteableBitmap(new PixelSize(256, 144), new Vector(96, 96));
            var media = new MediaFileInfo("clip", "C:\\test\\clip.mp4", DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(5), 1, path, [], 640, 360, 60);
            var card = new ClipCardViewModel(media, "C:\\test");
            CardThumbnailCache.Store(path, 256, first);
            card.SetPreviewLifecycle(true, true);
            var blanks = 0;
            card.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(card.PreviewImage) && card.PreviewImage is null) blanks++; };
            for (var navigation = 0; navigation < 10; navigation++)
            {
                card.SetPreviewLifecycle(true, false);
                CardThumbnailCache.Clear();
                card.SetPreviewLifecycle(true, true);
                Assert.Same(first, card.PreviewImage); // No file exists: a repeat decode cannot pass.
            }
            card.SetPreviewLifecycle(true, false);
            card.RefreshPreviewImage();
            Assert.Same(first, card.PreviewImage);
            CardThumbnailCache.Store(path, 256, replacement);
            card.SetPreviewLifecycle(true, true);
            Assert.Same(replacement, card.PreviewImage);
            Assert.Equal(0, blanks);
            ClipCardViewModel.SetPreviewDecodeWidth(256, 2);
            CardThumbnailCache.Store(path, 512, first);
            card.SetPreviewLifecycle(true, true);
            Assert.Same(first, card.PreviewImage);
            card.SetPreviewLifecycle(false, false);
            Assert.Null(card.PreviewImage);
            CardThumbnailCache.Clear();
        }, TimeSpan.FromSeconds(10), "Thumbnail lifecycle timed out.");
    }
}
