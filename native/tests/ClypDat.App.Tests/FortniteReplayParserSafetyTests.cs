using System.Text;
using ClypDat.App.Services;
using Unreal.Core;
using Unreal.Core.Models;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FortniteReplayParserSafetyTests
{
    private static readonly DateTime Start = new(2026, 10, 10, 8, 16, 3, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, false)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    [InlineData(24, true)]
    [InlineData(32, true)]
    public void FinishedEncryptedReplayWaitsForAUsableKey(int keyBytes, bool ready)
    {
        var header = new FortniteReplayHeader("match.replay", 100, Start, Start, 300, false, true, 0, keyBytes);
        Assert.Equal(ready, header.IsReady);
        Assert.False((header with { IsLive = true }).IsReady);
        Assert.False((header with { LengthSeconds = 0 }).IsReady);
    }

    [Fact]
    public void FutureFileVersionsAreRejectedBeforeReadingTheirLayout()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(0x1CA2E27Fu);
            writer.Write(8u);
        }
        Assert.Throws<InvalidDataException>(() => FortniteReplayFile.ReadForParsing(stream, 0, true));
    }

    [Fact]
    public void FinalisedReplayWithAPartialChunkDoesNotBecomeASuccessfulEmptyMatch()
    {
        using var stream = PlainReplayInfo();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(3u);
            writer.Write(4096);
            writer.Write(new byte[8]);
        }
        Assert.Throws<InvalidDataException>(() => FortniteReplayFile.ReadForParsing(stream, 0, true));
    }

    [Fact]
    public void OversizedActorPayloadIsRejectedBeforeTheLibraryAllocatesItsClaimedSize()
    {
        // Content header: no rep layout, actor on this channel. Unreal's
        // packed integer then claims nearly 2 billion bits in a six-byte bunch.
        var packed = new List<bool> { false, true };
        var remaining = uint.MaxValue / 2;
        do
        {
            var part = (byte)((remaining & 0x7f) << 1);
            remaining >>= 7;
            if (remaining != 0) part |= 1;
            for (var bit = 0; bit < 8; bit++) packed.Add((part & (1 << bit)) != 0);
        } while (remaining != 0);
        var bytes = new byte[(packed.Count + 7) / 8];
        for (var bit = 0; bit < packed.Count; bit++)
            if (packed[bit]) bytes[bit / 8] |= (byte)(1 << (bit % 8));

        var archive = new NetBitReader();
        archive.FillBuffer(bytes.AsMemory());
        var bunch = new DataBunch { Archive = archive, ChIndex = 0 };
        var reader = new FortniteReplayParser.GameStateReader(TimeSpan.FromSeconds(5), 128 * 1024 * 1024, 300);
        var before = GC.GetAllocatedBytesForCurrentThread();
        reader.ReadContentBlockPayload(bunch, out _, out _, out var payload);

        Assert.Null(payload);
        Assert.True(archive.IsError);
        Assert.True(reader.Incomplete);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024 * 1024);
    }

    [Fact]
    public void JoiningPlayersAreTrimmedWithoutInventingAReplayZeroCount()
    {
        MatchCountStep[] samples = [new(0.4, 45), new(1, 60), new(3.6, 96), new(120, 84), new(150, 87), new(300, 12)];
        var history = FortniteReplayParser.TrimJoiningPlayers(samples);
        Assert.Equal(samples.Skip(2), history);
        Assert.Equal(3.6, history[0].Seconds);
        Assert.Contains(history, sample => sample.Count == 87); // observed reboots can increase the count
    }

    [Fact]
    public void UnknownOwnerRetainsMatchSummaryButDoesNotInventZeroClipEliminations()
    {
        var data = Data();
        var match = FortniteMatchStats.Build(data, null, null);
        Assert.True(match.HasMatchSummary);
        Assert.False(match.LocalEventsAvailable);
        Assert.Equal("2", FortniteStatsPresentation.Tiles(match)[1].Value);
        Assert.Equal(["—", "—"], FortniteStatsPresentation.ClipTiles(match, new(0, 300, 1)).Select(tile => tile.Value));
        Assert.Empty(match.Events);
    }

    [Fact]
    public void IncompleteNetworkHistoryDoesNotFeedThePlacementChart()
    {
        var data = Data() with { PlayersLeft = [new(5, 100), new(20, 95)], PlayersLeftComplete = false };
        var match = FortniteMatchStats.Build(data, "00112233445566778899AABBCCDDEEFF", null);
        Assert.Empty(match.PlayersLeft);
        Assert.Null(match.PlayersLeftAtEnd);
        Assert.Equal(string.Empty, FortniteStatsPresentation.PlacementSummary(match));
    }

    [Fact]
    public void NonexistentDaylightSavingTimeCannotSilentlyShiftAClipByAnHour()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time");
        Assert.Throws<ArgumentException>(() => FortniteReplayFile.LocalToUtc(new(2026, 10, 4, 2, 30, 0), sydney));
    }

    [Fact]
    public void ModernOutfitReferenceReadsTheExactObservedNetGuid()
    {
        // Recorder PlayerState 626 in the 17:44:56 replay references NetGUID
        // 1393, whose exported asset path is character_slumpafter (Noelle).
        var field = new NetBitReader(new byte[] { 0xE3, 0x14 });
        Assert.Equal(1393u, FortniteReplayParser.ReadOutfitReference(field, 16));
        Assert.True(field.AtEnd());
    }

    [Theory]
    [InlineData("character_slumpafter", true)]
    [InlineData("Character_MajorSpeech", true)]
    [InlineData("CID_883_Athena_Commando_M_ChOneJonesy", true)]
    [InlineData("HID_001_Athena_Commando_F", false)]
    [InlineData("../character_slumpafter", false)]
    [InlineData("https://example.com/character_slumpafter", false)]
    [InlineData(null, false)]
    public void OnlyVerifiedOutfitAssetIdFamiliesAreAccepted(string? id, bool expected)
        => Assert.Equal(expected, FortniteReplayParser.IsOutfitId(id));

    [Fact]
    public void EmptyOrTruncatedReferencesDoNotProduceAnOutfit()
    {
        Assert.Null(FortniteReplayParser.ReadOutfitReference(new NetBitReader(new byte[] { 0 }), 8));
        Assert.Null(FortniteReplayParser.ReadOutfitReference(new NetBitReader(new byte[] { 0xE3 }), 8));
        Assert.Null(FortniteReplayParser.ReadOutfitReference(new NetBitReader(new byte[] { 0xE3 }), 16));
    }

    [Fact]
    public void ParsedOutfitSurvivesProjectionAndInvalidatesTheOlderReplayCache()
    {
        var data = Data() with { OutfitId = "character_slumpafter" };
        Assert.Equal(data.OutfitId, FortniteMatchStats.Build(data, null, null).OutfitId);
        Assert.EndsWith($"|parser-{FortniteReplayParser.CurrentVersion}", data.Header.CacheKey);
        Assert.NotEqual($"{data.Header.FileName}|{data.Header.SizeBytes}|{data.Header.LastWriteUtc.Ticks}", data.Header.CacheKey);
    }

    private static FortniteReplayData Data() => new(
        new("match.replay", 100, Start, Start, 300, false, false, 0),
        new(2, 1, 0.38, 900, 300, 400, 0, 0, 452100, 0, 210), 12, 100, [], "Playlist_NoBuildBR_Solo", 0);

    private static MemoryStream PlainReplayInfo()
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(0x1CA2E27Fu);
        writer.Write(6u);
        writer.Write(300_000u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(1); // empty FString, including its terminator
        writer.Write((byte)0);
        writer.Write(0u);
        writer.Write(DateTime.SpecifyKind(Start, DateTimeKind.Unspecified).ToBinary());
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        return stream;
    }
}
