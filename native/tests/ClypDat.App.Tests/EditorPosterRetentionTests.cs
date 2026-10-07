using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorPosterRetentionTests
{
    [Fact]
    public void SameClipRefresh_KeepsLoadingPosterUntilReplacementDecodes()
    {
        AvaloniaTestThread.Run(() =>
        {
            var path = $"poster-{Guid.NewGuid():N}.jpg";
            using var poster = new WriteableBitmap(new PixelSize(960, 540), new Vector(96, 96));
            using var replacement = new WriteableBitmap(new PixelSize(960, 540), new Vector(96, 96));
            using var cardPreview = new WriteableBitmap(new PixelSize(256, 144), new Vector(96, 96));
            var model = NewModel();
            try
            {
                BitmapCache.Store(path, poster);
                model.SelectMediaThumbnail("C:\\test\\clip.mp4", path);
                model.IsEditorVideoLoading = true;
                var blanks = 0;
                model.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(model.SelectedThumbnail) && model.SelectedThumbnail is null) blanks++;
                };

                BitmapCache.Invalidate(path);
                model.SelectMediaThumbnail("C:\\test\\CLIP.mp4", path, cardPreview);
                Assert.Same(poster, model.SelectedThumbnail);
                model.SelectMediaThumbnail("C:\\test\\clip.mp4", string.Empty);
                Assert.Same(poster, model.SelectedThumbnail);
                Assert.Equal(path, model.SelectedThumbnailPath);
                Assert.True(model.HasSelectedThumbnail);

                BitmapCache.Store(path, replacement);
                model.SelectMediaThumbnail("C:\\test\\clip.mp4", path);
                Assert.Same(replacement, model.SelectedThumbnail);
                Assert.Equal(0, blanks);
            }
            finally { BitmapCache.Invalidate(path); }
        }, TimeSpan.FromSeconds(10), "Editor poster refresh timed out.");
    }

    [Fact]
    public void QuickOpen_ReusesCardImageWhileEditorThumbnailIsUnavailable()
    {
        AvaloniaTestThread.Run(() =>
        {
            var path = $"poster-{Guid.NewGuid():N}.jpg";
            using var poster = new WriteableBitmap(new PixelSize(256, 144), new Vector(96, 96));
            var media = new MediaFileInfo("clip", "C:\\test\\clip.mp4", DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(5), 1, path, [], 640, 360, 60);
            var card = new ClipCardViewModel(media, "C:\\test");
            var model = NewModel();
            try
            {
                ClipCardViewModel.SetPreviewDecodeWidth(256, 1);
                CardThumbnailCache.Store(path, 256, poster);
                card.SetPreviewLifecycle(true, true);
                Assert.Same(poster, card.PreviewImage);

                model.SelectMediaThumbnail(media.Path, media.ThumbnailPath, card.PreviewImage);
                model.IsEditorVideoLoading = true;
                card.SetPreviewLifecycle(false, false);
                CardThumbnailCache.Invalidate(path);
                model.SelectMediaThumbnail(media.Path, media.ThumbnailPath);

                Assert.Same(poster, model.SelectedThumbnail);
                Assert.True(model.HasSelectedThumbnail);
            }
            finally { CardThumbnailCache.Invalidate(path); }
        }, TimeSpan.FromSeconds(10), "Editor card fallback timed out.");
    }

    [Fact]
    public void DifferentClipWithoutThumbnail_ClearsPreviousPoster()
    {
        AvaloniaTestThread.Run(() =>
        {
            using var first = new WriteableBitmap(new PixelSize(256, 144), new Vector(96, 96));
            using var second = new WriteableBitmap(new PixelSize(256, 144), new Vector(96, 96));
            var model = NewModel();
            model.SelectMediaThumbnail("C:\\test\\first.mp4", string.Empty, first);
            model.IsEditorVideoLoading = true;

            model.SelectMediaThumbnail("C:\\test\\second.mp4", string.Empty);
            Assert.Null(model.SelectedThumbnail);
            Assert.False(model.HasSelectedThumbnail);
            model.SelectMediaThumbnail("C:\\test\\second.mp4", string.Empty, second);
            Assert.Same(second, model.SelectedThumbnail);
        }, TimeSpan.FromSeconds(10), "Editor poster selection timed out.");
    }

    // Skip capture, account and hardware services; exercise the poster selection
    // used by OpenMedia, image hydration and thumbnail regeneration.
    private static MainWindowViewModel NewModel() =>
        (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
}
