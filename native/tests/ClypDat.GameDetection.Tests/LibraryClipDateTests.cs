using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class LibraryClipDateTests
{
    [Theory]
    [InlineData(0, "Just now")]
    [InlineData(1, "1 min ago")]
    [InlineData(59, "59 mins ago")]
    [InlineData(60, "1 hour ago")]
    [InlineData(1439, "23 hours ago")]
    [InlineData(1440, "1 day ago")]
    [InlineData(29 * 1440, "29 days ago")]
    public void RelativeDateFormatter_UsesLiveAge(int minutes, string expected)
    {
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, ClipCardViewModel.FormatRelativeDate(now.AddMinutes(-minutes), now));
    }

    [Fact]
    public void RelativeDateFormatter_UsesAbsoluteDateAfterThirtyDays()
    {
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddDays(-30).ToString("MMM d, yyyy"), ClipCardViewModel.FormatRelativeDate(now.AddDays(-30), now));
    }

    [Theory]
    [InlineData("FortniteClient-Win64-Shipping", "Fortnite")]
    [InlineData("FortniteClient-Win64-Shipping (Trimmed)", "Fortnite")]
    [InlineData("DESKTOPCAPTURE", "Desktop Capture")]
    [InlineData("Desktop", "Desktop Capture")]
    [InlineData("RobloxPlayerBeta", "Roblox")]
    [InlineData("VALORANT-Win64-Shipping.exe", "Valorant")]
    [InlineData("LeagueClientUx.exe", "League of Legends")]
    public void NormalizeGameDisplayName_UsesCanonicalBuckets(string input, string expected)
    {
        Assert.Equal(expected, ClipCardViewModel.NormalizeGameDisplayName(input));
    }

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
