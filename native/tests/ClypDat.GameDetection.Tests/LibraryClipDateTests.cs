using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class LibraryClipDateTests
{
    [Fact]
    public void LateSidecarUpdate_NotifiesDateHeaderLabel()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-date-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "Clips", "Game", "clip.mp4");
        try
        {
            var initial = CreateMedia(path, new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
            var clip = new ClipCardViewModel(initial, root);
            var changed = new List<string?>();
            clip.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            ClipInfoSidecar.Save(root, path, new ClipInfo(
                "Game", null, CapturedAt: new DateTimeOffset(2026, 2, 12, 12, 0, 0, TimeSpan.Zero),
                SteelSeriesImportKey: "moments-key"));
            clip.UpdateMedia(initial);

            Assert.Contains(nameof(ClipCardViewModel.DateHeaderLabel), changed);
            Assert.Equal(clip.CreatedAt.ToLocalTime().ToString("ddd, MMM d").ToUpperInvariant(), clip.DateHeaderLabel);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindSortedClipIndex_RepositionsLateDateCard()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-order-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stale = new ClipCardViewModel(CreateMedia(Path.Combine(root, "stale.mp4"), new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero)), root);
            var corrected = new ClipCardViewModel(CreateMedia(Path.Combine(root, "corrected.mp4"), new DateTimeOffset(2026, 2, 12, 12, 0, 0, TimeSpan.Zero)), root);
            var cards = new[] { stale, corrected };

            corrected.UpdateMedia(CreateMedia(corrected.Path, new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)), reloadSidecars: false);

            Assert.Equal(0, MainWindowViewModel.FindSortedClipIndex(cards, corrected));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static MediaFileInfo CreateMedia(string path, DateTimeOffset createdAt) => new(
        Path.GetFileName(path), path, createdAt, TimeSpan.Zero, 0, string.Empty,
        Array.Empty<MediaTrackInfo>(), 0, 0, 0);
}
