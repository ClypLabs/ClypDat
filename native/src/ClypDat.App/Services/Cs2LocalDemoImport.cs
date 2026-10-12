using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ClypDat.Core.Settings;
using Google.Protobuf;

namespace ClypDat.App.Services;

internal sealed record Cs2LocalDemoImportResult(string Message, bool Saved = false, bool Linked = false);
internal sealed record Cs2LocalDemoInfo(string? MatchId, DateTimeOffset? MatchTime);

// Saved demos do not need Steam's match-history API. Their original files are
// read only; an archive is copied before Unpack, which deletes its own input.
// File creation/write times are deliberately never used as match timestamps.
internal static class Cs2LocalDemoImport
{
    internal const int MaximumInfoBytes = 256 * 1024;
    private static string ScratchRoot => Path.Combine(AppDataPaths.Root, "cs2-local-import");

    public static Task<Cs2LocalDemoImportResult> ImportAsync(string demoPath, string? localSteamId, MatchStatsStore store, CancellationToken cancellationToken) =>
        ImportAsync(demoPath, localSteamId, store, cancellationToken, Cs2DemoStatsParser.ParseAsync,
            () => MemoryTrimmer.GameRunning, ScratchRoot);

    // The seam keeps filesystem handling, decompression and matching real in
    // tests while replacing only the costly demo decoder and the game probe.
    internal static async Task<Cs2LocalDemoImportResult> ImportAsync(string demoPath, string? localSteamId, MatchStatsStore store,
        CancellationToken cancellationToken, Func<string, string?, CancellationToken, Task<MatchStats>> parser,
        Func<bool> isGameRunning, string scratchRoot, long maximumSourceBytes = Cs2DemoDownloader.MaximumDemoBytes,
        long maximumDemoBytes = Cs2DemoDownloader.MaximumDemoBytes)
    {
        try
        {
            while (isGameRunning()) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<Cs2LocalDemoImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() =>
            {
                try
                {
                    // A game can start between the queued continuation and
                    // this thread. Wait again before any copying or parsing.
                    while (isGameRunning())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        cancellationToken.WaitHandle.WaitOne(500);
                    }
                    completion.SetResult(Import(demoPath, localSteamId, store, cancellationToken, parser, scratchRoot,
                        maximumSourceBytes, maximumDemoBytes));
                }
                catch (OperationCanceledException) { completion.SetResult(new("Demo import cancelled.")); }
                catch (Exception error)
                {
                    AppLog.Info($"Saved CS2 demo import failed ({error.GetType().Name}).");
                    completion.SetResult(new("This demo couldn't be imported. It may be incomplete or use a CS2 version ClypDat doesn't support."));
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Saved CS2 demo reader" }.Start();
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new("Demo import cancelled."); }
    }

    private static Cs2LocalDemoImportResult Import(string demoPath, string? localSteamId, MatchStatsStore store,
        CancellationToken cancellationToken, Func<string, string?, CancellationToken, Task<MatchStats>> parser,
        string scratchRoot, long maximumSourceBytes, long maximumDemoBytes)
    {
        var compressed = demoPath.EndsWith(".dem.bz2", StringComparison.OrdinalIgnoreCase);
        if (!compressed && !demoPath.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
            return new("Choose a CS2 .dem or .dem.bz2 file.");
        localSteamId = Cs2MatchHistoryFormats.NormaliseSteamId(localSteamId);
        string? ownedFolder = null;
        try
        {
            // Keep this handle open throughout parsing so the selected file
            // cannot be replaced, written to or deleted during the import.
            using var source = new FileStream(demoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            var sourceCap = Math.Min(maximumSourceBytes, compressed ? Cs2DemoDownloader.MaximumCompressedBytes : maximumDemoBytes);
            if (source.Length == 0) return new("This demo is empty.");
            if (source.Length > sourceCap) return new("This demo is larger than ClypDat can import.");
            var info = ReadCompanionInfo(demoPath);
            var parsePath = demoPath;
            if (compressed)
            {
                ownedFolder = Path.Combine(Path.GetFullPath(scratchRoot), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ownedFolder);
                var archive = Path.Combine(ownedFolder, "match.dem.bz2");
                using (var copy = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    if (!Cs2DemoDownloader.CopyCapped(source, copy, sourceCap, cancellationToken))
                        return new("This demo is larger than ClypDat can import.");
                var unpacked = Cs2DemoDownloader.Unpack(archive, cancellationToken);
                if (unpacked.Outcome != Cs2DemoDownloadOutcome.Ready) return new("This demo is larger than ClypDat can import.");
                parsePath = unpacked.DemoPath!;
                if (new FileInfo(parsePath).Length > maximumDemoBytes) return new("This demo is larger than ClypDat can import.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = parser(parsePath, localSteamId, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (parsed.Game != "cs2" || parsed.Rounds.Count == 0 || parsed.Players.Count == 0 ||
                parsed.DemoDurationSeconds is not { } duration || !double.IsFinite(duration) || duration <= 0 || duration > 24 * 3600)
                return new("No completed CS2 match could be read from this demo.");

            var live = FindLiveMatch(store.LoadAll(), parsed, info, localSteamId);
            if (live is not null)
            {
                var merged = MatchClipMapping.Merge(live, parsed with { MatchId = info?.MatchId ?? live.MatchId });
                store.Save(merged);
                return new("Demo stats imported. Clips from this match now show the full scoreboard and radar.", Saved: true, Linked: true);
            }

            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(parsed))).ToLowerInvariant()[..24];
            var start = info?.MatchTime?.UtcDateTime ?? default;
            store.Save(parsed with
            {
                Id = info?.MatchId is { } id ? $"cs2-demo-{id}" : $"cs2-local-{fingerprint}",
                MatchId = info?.MatchId,
                StartedUtc = start,
                EndedUtc = start == default ? default : start.AddSeconds(duration)
            });
            return start == default
                ? new("Demo stats saved, but its match time is unavailable, so ClypDat couldn't safely link it to your clips. Keep the matching .dem.info file beside it and import again.", Saved: true)
                : new("Demo stats imported using its recorded match time. Clips from that match can now show them.", Saved: true, Linked: true);
        }
        finally
        {
            if (ownedFolder is not null)
            {
                // Only the two files this job owns, never a recursive sweep
                // of the shared AppData directory or the selected demo folder.
                Cs2DemoDownloader.TryDelete(Path.Combine(ownedFolder, "match.dem.bz2"));
                Cs2DemoDownloader.TryDelete(Path.Combine(ownedFolder, "match.dem"));
                try { Directory.Delete(ownedFolder); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static MatchStats? FindLiveMatch(IEnumerable<MatchStats> matches, MatchStats demo, Cs2LocalDemoInfo? info, string? localSteamId)
    {
        var candidates = matches.Where(match => match.Game == "cs2" && match.HasLiveData)
            .Where(match => localSteamId is null || match.LocalSteamId == localSteamId)
            .Where(match => match.MatchId is null || info?.MatchId is null || match.MatchId == info.MatchId)
            .Where(match => !string.IsNullOrEmpty(match.Map) && !string.IsNullOrEmpty(demo.Map) &&
                            string.Equals(match.Map, demo.Map, StringComparison.OrdinalIgnoreCase))
            .Where(match => info?.MatchTime is { } time
                ? MatchClipMapping.IsSameMatch(match, demo, time)
                : SameSignature(match, demo)).ToArray();
        if (candidates.Length == 1) return candidates[0];
        // The time window alone can cover back-to-back games on one map.
        // Exact rounds plus the local player's final counters can distinguish
        // them; two equally corroborated candidates still must not merge.
        var corroborated = candidates.Where(match => SameSignature(match, demo)).ToArray();
        return corroborated.Length == 1 ? corroborated[0] : null;
    }

    private static bool SameSignature(MatchStats live, MatchStats demo)
    {
        if (live.Rounds.Count < 4 || live.Rounds.Count != demo.Rounds.Count || live.LocalSteamId is not { } steamId ||
            live.LocalPlayer is not { } local || demo.Players.FirstOrDefault(player => player.SteamId == steamId) is not { } recorded)
            return false;
        if (local.Kills != recorded.Kills || local.Deaths != recorded.Deaths || local.Assists != recorded.Assists) return false;
        var liveRounds = live.Rounds.OrderBy(round => round.Number).ToArray();
        var demoRounds = demo.Rounds.OrderBy(round => round.Number).ToArray();
        return liveRounds.Select((round, index) => round.Number == demoRounds[index].Number &&
            round.WinnerSide is "CT" or "T" && round.WinnerSide == demoRounds[index].WinnerSide).All(equal => equal);
    }

    internal static Cs2LocalDemoInfo? ReadCompanionInfo(string demoPath)
    {
        var infoPath = (demoPath.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase) ? Path.ChangeExtension(demoPath, null) : demoPath) + ".info";
        try
        {
            using var source = new FileStream(infoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length == 0 || source.Length > MaximumInfoBytes) return null;
            var bytes = new byte[(int)source.Length];
            source.ReadExactly(bytes);
            return ParseInfo(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidProtocolBufferException) { return null; }
    }

    // Valve's CDataGCCStrike15_v2_MatchInfo: uint64 matchid=1,
    // uint32 matchtime=2. Unknown fields are skipped by the protobuf reader.
    // https://github.com/SteamDatabase/Protobufs/blob/master/csgo/cstrike15_gcmessages.proto
    internal static Cs2LocalDemoInfo? ParseInfo(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumInfoBytes) return null;
        try
        {
            using var input = new CodedInputStream(bytes);
            ulong? matchId = null;
            uint? matchTime = null;
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 8:
                        if (matchId is not null) return null;
                        matchId = input.ReadUInt64();
                        break;
                    case 16:
                        if (matchTime is not null) return null;
                        matchTime = input.ReadUInt32();
                        break;
                    default:
                        if (WireFormat.GetTagFieldNumber(tag) is 1 or 2) return null;
                        input.SkipLastField();
                        break;
                }
            }
            DateTimeOffset? time = matchTime is >= 1356998400 && matchTime <= DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()
                ? DateTimeOffset.FromUnixTimeSeconds(matchTime.Value) : null;
            return matchId is > 0 || time is not null
                ? new(matchId is > 0 ? matchId.Value.ToString(CultureInfo.InvariantCulture) : null, time)
                : null;
        }
        catch (InvalidProtocolBufferException) { return null; }
    }
}
