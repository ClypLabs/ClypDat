using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LibraryThumbnailRetentionTests
{
    [Fact]
    public void SettledHiddenLibraryCacheClearPreservesEditorPosters()
    {
        AvaloniaTestThread.Run(() =>
        {
            var path = $"editor-retention-{Guid.NewGuid()}.jpg";
            ClipCardViewModel.SetPreviewDecodeWidth(256, 1);
            using var cardImage = new WriteableBitmap(new PixelSize(256, 144), new Vector(96, 96));
            using var editorPoster = new WriteableBitmap(new PixelSize(960, 540), new Vector(96, 96));
            var media = new MediaFileInfo("clip", "C:\\test\\clip.mp4", DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(5), 1, path, [], 640, 360, 60);
            var card = new ClipCardViewModel(media, "C:\\test");
            var editor = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
            var fallbackEditor = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
            try
            {
                CardThumbnailCache.Store(path, 256, cardImage);
                BitmapCache.Store(path, editorPoster);
                card.SetPreviewLifecycle(true, true);
                editor.SelectMediaThumbnail(media.Path, path, card.PreviewImage);
                fallbackEditor.SelectMediaThumbnail(media.Path, string.Empty, card.PreviewImage);
                editor.IsEditorVideoLoading = fallbackEditor.IsEditorVideoLoading = true;
                card.SetPreviewLifecycle(true, false);
                Assert.Same(cardImage, card.PreviewImage); // grace period stays warm

                card.ReleaseInactivePreviewImage();
                CardThumbnailCache.Clear();
                Dispatcher.UIThread.RunJobs(); // execute the render fence

                Assert.Null(card.PreviewImage);
                Assert.Null(CardThumbnailCache.Get(path, 256));
                Assert.Same(editorPoster, editor.SelectedThumbnail);
                Assert.True(BitmapCache.TryGet(path, out var cachedPoster));
                Assert.Same(editorPoster, cachedPoster);
                Assert.Same(cardImage, fallbackEditor.SelectedThumbnail);
                using var fullSizePixels = editorPoster.Lock();
                using var fallbackPixels = cardImage.Lock();
                Assert.NotEqual(IntPtr.Zero, fullSizePixels.Address);
                Assert.NotEqual(IntPtr.Zero, fallbackPixels.Address);
            }
            finally
            {
                CardThumbnailCache.Invalidate(path);
                BitmapCache.Invalidate(path);
            }
        }, TimeSpan.FromSeconds(10), "Hidden library cache release timed out.");
    }

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
