using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Google.Protobuf;
using ICSharpCode.SharpZipLib.BZip2;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class Cs2LocalDemoImportTests
{
    private const string SteamId = "76561198000000001";
    private static readonly DateTime Start = new(2020, 5, 12, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Files : IDisposable
    {
        private static readonly string TestRoot = Path.Combine(AppDataPaths.Root, "cs2-local-import-tests");
        public string Root { get; } = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        public string Scratch => Path.Combine(Root, "scratch");
        public MatchStatsStore Store { get; }

        public Files()
        {
            Directory.CreateDirectory(Root);
            Store = new MatchStatsStore(Path.Combine(Root, "library"));
        }

        public string Demo(byte[]? bytes = null)
        {
            var path = Path.Combine(Root, "saved.dem");
            File.WriteAllBytes(path, bytes ?? "PBDEMS2 fixture"u8.ToArray());
            return path;
        }

        public string Archive(byte[] bytes)
        {
            var path = Path.Combine(Root, "saved.dem.bz2");
            using var file = File.Create(path);
            using var compressed = new BZip2OutputStream(file);
            compressed.Write(bytes);
            return path;
        }

        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            if (absolute.StartsWith(Path.GetFullPath(TestRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(absolute, recursive: true);
        }
    }

    private static byte[] Info(ulong id = 1234567, uint? time = null)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.WriteTag(1, WireFormat.WireType.Varint);
            output.WriteUInt64(id);
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteUInt32(time ?? checked((uint)new DateTimeOffset(Start).ToUnixTimeSeconds()));
            output.WriteTag(3, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFromUtf8("unknown nested field"));
        }
        return stream.ToArray();
    }

    private static MatchStats Demo() => new()
    {
        Game = "cs2", Map = "de_mirage", HasDemoData = true, DemoStatus = "parsed", DemoDurationSeconds = 1000,
        LocalSteamId = SteamId, LocalPlayerName = "Local", LocalTeamId = "CT",
        Players = [new() { SteamId = SteamId, IsLocal = true, Name = "Local", Kills = 8, Deaths = 3, Assists = 2, Damage = 999 }],
        Rounds = Enumerable.Range(1, 4).Select(round => new MatchRound
        {
            Number = round, WinnerSide = round % 2 == 0 ? "T" : "CT", DemoSeconds = round * 200
        }).ToArray(),
        Events = [new() { Kind = "kill", Round = 1, DemoSeconds = 40 }]
    };

    private static MatchStats Live(string id = "gsi-one") => Demo() with
    {
        Id = id, HasLiveData = true, HasDemoData = false, DemoStatus = null, StartedUtc = Start, EndedUtc = Start.AddMinutes(17),
        Players = [Demo().Players[0] with { Damage = null }],
        Rounds = Demo().Rounds.Select(round => round with { EndedUtc = Start.AddSeconds(round.DemoSeconds!.Value) }).ToArray()
    };

    private static Task<Cs2LocalDemoImportResult> Import(Files files, string path,
        Func<string, string?, CancellationToken, Task<MatchStats>>? parser = null, CancellationToken cancellationToken = default,
        long maximumSourceBytes = Cs2DemoDownloader.MaximumDemoBytes, long maximumDemoBytes = Cs2DemoDownloader.MaximumDemoBytes) =>
        Cs2LocalDemoImport.ImportAsync(path, SteamId, files.Store, cancellationToken,
            parser ?? ((_, _, _) => Task.FromResult(Demo())), () => false, files.Scratch, maximumSourceBytes, maximumDemoBytes);

    [Fact]
    public void CompanionMetadataReadsValveFieldsAndRejectsMalformedOrOversizedInput()
    {
        var info = Cs2LocalDemoImport.ParseInfo(Info());
        Assert.Equal("1234567", info!.MatchId);
        Assert.Equal(new DateTimeOffset(Start), info.MatchTime);
        Assert.Null(Cs2LocalDemoImport.ParseInfo([8, 0x80]));
        Assert.Null(Cs2LocalDemoImport.ParseInfo([10, 1, 1])); // matchid has the wrong wire type
        Assert.Null(Cs2LocalDemoImport.ParseInfo(new byte[Cs2LocalDemoImport.MaximumInfoBytes + 1]));
        Assert.Null(Cs2LocalDemoImport.ParseInfo(Info(0, 0)));
        Assert.Null(Cs2LocalDemoImport.ParseInfo(Info(time: uint.MaxValue))!.MatchTime);
    }

    [Fact]
    public async Task PlainDemoAndInfoRemainUntouchedAndMetadataTimeOverridesFileTimes()
    {
        using var files = new Files();
        var path = files.Demo();
        var metadata = Info();
        File.WriteAllBytes(path + ".info", metadata);
        File.SetLastWriteTimeUtc(path, Start.AddYears(5));
        var original = File.ReadAllBytes(path);
        var originalWrite = File.GetLastWriteTimeUtc(path);
        var result = await Import(files, path, (source, _, _) =>
        {
            Assert.Equal(path, source);
            Assert.Equal(ThreadPriority.BelowNormal, Thread.CurrentThread.Priority);
            Assert.Throws<IOException>(() => File.Open(source, FileMode.Open, FileAccess.Write).Dispose());
            return Task.FromResult(Demo());
        });
        Assert.True(result.Saved);
        Assert.True(result.Linked);
        var match = Assert.Single(files.Store.LoadAll());
        Assert.Equal(Start, match.StartedUtc);
        Assert.Equal(Start.AddSeconds(1000), match.EndedUtc);
        Assert.Equal("1234567", match.MatchId);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(metadata, File.ReadAllBytes(path + ".info"));
        Assert.Equal(originalWrite, File.GetLastWriteTimeUtc(path));
        Assert.False(Directory.Exists(files.Scratch));
    }

    [Fact]
    public async Task CompressedDemoParsesPrivateCopyAndDeletesOnlyOwnedScratch()
    {
        using var files = new Files();
        var bytes = "PBDEMS2 decompressed fixture"u8.ToArray();
        var path = files.Archive(bytes);
        var original = File.ReadAllBytes(path);
        File.WriteAllBytes(Path.ChangeExtension(path, null) + ".info", Info());
        Directory.CreateDirectory(files.Scratch);
        var unrelated = Path.Combine(files.Scratch, "pre-existing.dem");
        File.WriteAllText(unrelated, "preserve");
        string? parsePath = null;
        var result = await Import(files, path, (source, _, _) =>
        {
            parsePath = source;
            Assert.NotEqual(path, source);
            Assert.StartsWith(files.Scratch + Path.DirectorySeparatorChar, source);
            Assert.Equal(bytes, File.ReadAllBytes(source));
            return Task.FromResult(Demo());
        });
        Assert.True(result.Saved);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
        Assert.False(File.Exists(parsePath));
        Assert.Empty(Directory.GetDirectories(files.Scratch));
    }

    [Fact]
    public async Task MissingCompanionTimeNeverUsesCreationOrModifiedTime()
    {
        using var files = new Files();
        var path = files.Demo();
        File.SetLastWriteTimeUtc(path, Start);
        var result = await Import(files, path);
        Assert.True(result.Saved);
        Assert.False(result.Linked);
        Assert.Contains("match time is unavailable", result.Message);
        var match = Assert.Single(files.Store.LoadAll());
        Assert.Equal(default, match.StartedUtc);
        Assert.Equal(default, match.EndedUtc);
        Assert.Null(MatchClipMapping.FindMatch([match], new(Start, Start.AddSeconds(30), 30, null, true, [])));
    }

    [Fact]
    public async Task UniqueGsiSignatureMergesWithoutCompanionTime()
    {
        using var files = new Files();
        files.Store.Save(Live());
        var result = await Import(files, files.Demo());
        Assert.True(result.Linked);
        var match = Assert.Single(files.Store.LoadAll());
        Assert.Equal("gsi-one", match.Id);
        Assert.Equal(Start, match.StartedUtc);
        Assert.True(match.HasLiveData);
        Assert.True(match.HasDemoData);
        Assert.Equal(999, match.LocalPlayer!.Damage);
    }

    [Fact]
    public void AmbiguousOrUncorroboratedSignaturesDoNotMerge()
    {
        Assert.Null(Cs2LocalDemoImport.FindLiveMatch([Live(), Live("gsi-two")], Demo(), null, SteamId));
        Assert.Null(Cs2LocalDemoImport.FindLiveMatch([Live() with { Map = "de_inferno" }], Demo(), null, SteamId));
        Assert.Null(Cs2LocalDemoImport.FindLiveMatch([Live() with { Players = [Live().Players[0] with { Kills = 9 }] }], Demo(), null, SteamId));
        Assert.Null(Cs2LocalDemoImport.FindLiveMatch([Live() with { Rounds = Live().Rounds.Take(3).ToArray() }], Demo(), null, SteamId));
        Assert.Null(Cs2LocalDemoImport.FindLiveMatch([Live() with { LocalSteamId = "76561198000000002" }], Demo(), null, SteamId));
    }

    [Fact]
    public void MetadataTimeStillRequiresMatchingMapAndRoundResults()
    {
        var info = new Cs2LocalDemoInfo("1234567", new DateTimeOffset(Start));
        Assert.NotNull(Cs2LocalDemoImport.FindLiveMatch([Live()], Demo(), info, SteamId));
        Assert.Null(Cs2LocalDemoImport.FindLiveMatch([Live() with { StartedUtc = Start.AddDays(1), EndedUtc = Start.AddDays(1).AddMinutes(17) }], Demo(), info, SteamId));
        Assert.Null(Cs2LocalDemoImport.FindLiveMatch([Live() with { MatchId = "different" }], Demo(), info, SteamId));
        Assert.Null(Cs2LocalDemoImport.FindLiveMatch([Live()], Demo() with { Rounds = Demo().Rounds.Select(round => round with { WinnerSide = "T" }).ToArray() }, info, SteamId));
    }

    [Fact]
    public async Task SourceAndExpandedSizeCapsPreventParsingAndPreserveArchive()
    {
        using var files = new Files();
        var called = false;
        Task<MatchStats> Parse(string _, string? __, CancellationToken ___) { called = true; return Task.FromResult(Demo()); }
        var plain = await Import(files, files.Demo(new byte[100]), Parse, maximumSourceBytes: 50);
        Assert.False(plain.Saved);
        var path = files.Archive(new byte[1000]);
        var original = File.ReadAllBytes(path);
        var expanded = await Import(files, path, Parse, maximumDemoBytes: 500);
        Assert.False(expanded.Saved);
        Assert.False(called);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetDirectories(files.Scratch));
    }

    [Fact]
    public async Task CancellationAndParserFailureCleanPrivateFilesButPreserveSource()
    {
        using var files = new Files();
        var path = files.Archive("PBDEMS2 fixture"u8.ToArray());
        var original = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource();
        var cancelled = await Import(files, path, (_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Demo());
        }, cancellation.Token);
        Assert.Contains("cancelled", cancelled.Message);
        Assert.False(cancelled.Saved);
        var failed = await Import(files, path, (_, _, _) => throw new InvalidDataException("private source filename"));
        Assert.DoesNotContain("private source filename", failed.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetDirectories(files.Scratch));
        Assert.Empty(files.Store.LoadAll());
    }

    [Fact]
    public async Task GameRunningPostponesImportUntilIdleAndWaitingCanBeCancelled()
    {
        using var files = new Files();
        var running = 1;
        var called = 0;
        var path = files.Demo();
        var import = Cs2LocalDemoImport.ImportAsync(path, SteamId, files.Store, CancellationToken.None,
            (_, _, _) => { Interlocked.Increment(ref called); return Task.FromResult(Demo()); },
            () => Volatile.Read(ref running) != 0, files.Scratch);
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref called));
        Assert.False(import.IsCompleted);
        Volatile.Write(ref running, 0);
        Assert.True((await import).Saved);
        Assert.Equal(1, called);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await Cs2LocalDemoImport.ImportAsync(path, SteamId, files.Store, cancellation.Token,
            (_, _, _) => throw new InvalidOperationException("must not parse"), () => true, files.Scratch);
        Assert.Contains("cancelled", cancelled.Message);
    }
}
