using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class SteelSeriesImportServiceTests
{
    [Theory]
    [InlineData("Counter-Strike-2__2025-08-01__23-19-32_trim", "Counter Strike 2", 2025, 8, 1, 23, 19, 32)]
    [InlineData("Overwatch®__2026-07-27__23-54-18", "Overwatch®", 2026, 7, 27, 23, 54, 18)]
    public void TimestampedName_UsesFilenameCaptureTime(string stem, string expectedGame, int year, int month, int day, int hour, int minute, int second)
    {
        Assert.True(SteelSeriesImportService.TryParseTimestampedName(stem, out var game, out var capturedAt));
        Assert.Equal(expectedGame, game);
        Assert.Equal(new DateTime(year, month, day, hour, minute, second), capturedAt.LocalDateTime);
    }

    [Theory]
    [InlineData("Moments clip from Nov 11, 2025", "Backseat Drivers Demo", "Backseat Drivers Demo")]
    [InlineData("Moments Desktop clip from Feb 05, 2026", "Desktop", "Desktop")]
    [InlineData("Trimmed clip from Aug 01, 2025", "Counter-Strike 2", "Counter-Strike 2 (Trimmed)")]
    [InlineData("Auto-clip: Victory Royale", "Fortnite", "Auto-clip: Victory Royale")]
    public void NormalizeTitle_RemovesOnlyGenericMomentsTitles(string title, string game, string expected)
    {
        Assert.Equal(expected, SteelSeriesImportService.NormalizeTitle(title, game, "unused"));
    }

    [Fact]
    public void NormalizeTitle_UsesTrimmedMetadataForGenericTitle()
    {
        Assert.Equal("Counter-Strike 2 (Trimmed)", SteelSeriesImportService.NormalizeTitle("Moments clip from Nov 11, 2025", "Counter-Strike 2", "unused", isTrimmed: true));
    }

    [Theory]
    [InlineData("Auto-clip: Headshot kill", "auto", "headshot", "Headshot kill")]
    [InlineData("Auto-clip: Double Elimination", "auto", "double_kill", "Double Elimination")]
    [InlineData(null, "auto", "victory_royale", "Victory Royale")]
    [InlineData("Moments clip from Nov 11, 2025", "shortcut", "shortcut", null)]
    public void AutoClipMetadata_ExtractsReasonOnlyWhenAutomatic(string? title, string? triggerType, string? triggerName, string? expected)
    {
        Assert.Equal(expected, SteelSeriesImportService.GetAutoClipEventType(title, triggerType, triggerName));
    }

    [Theory]
    [InlineData("Moments clip from Dec 30, 2025", "Marvel Rivals", false)]
    [InlineData("Marvel Rivals", "Marvel Rivals", false)]
    [InlineData("Auto-clip: Headshot kill", "Counter-Strike 2", true)]
    [InlineData("A10 WARTHOG MAKES ME WET", "War Thunder", true)]
    public void TitleClassification_OnlyKeepsMeaningfulTitles(string title, string game, bool expected)
    {
        Assert.Equal(expected, SteelSeriesImportService.HasMeaningfulTitle(title, game, "unused"));
    }

    [Fact]
    public void NormalizeGame_MapsDesktopPlaceholder()
    {
        Assert.Null(SteelSeriesImportService.NormalizeGame("moments.desktopModeGameName"));
        Assert.Equal("Desktop Capture", SteelSeriesImportService.NormalizeGame("DESKTOPCAPTURE"));
    }

    [Fact]
    public void CatalogIdentity_UsesStableCatalogId()
    {
        var record = new SteelSeriesClipRecord(
            @"D:\missing\clip.mp4",
            null,
            "Fortnite",
            new DateTimeOffset(2026, 7, 27, 23, 54, 18, TimeSpan.FromHours(10)),
            "Victory Royale",
            "abc-123");

        Assert.Equal("id|abc-123", SteelSeriesImportService.GetImportKey(record));
    }

    [Fact]
    public void ImportHistory_RoundTripsDistinctKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-steelseries-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(SteelSeriesImportHistoryStore.TrySave(root, new[] { "b", "a", "a" }));
            Assert.True(SteelSeriesImportHistoryStore.TryLoad(root, out var keys));
            Assert.Equal(new[] { "a", "b" }, keys.OrderBy(key => key).ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
