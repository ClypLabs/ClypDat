using FortniteReplayReader;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClypDat.App.Services;

/// <summary>One elimination event from the replay, in replay seconds. Ids are Epic account ids, "Bot", or a named bot.</summary>
internal sealed record FortniteElimination(double Seconds, string? Eliminator, string? Eliminated, bool Knocked, bool EliminatedIsBot);

/// <summary>The recording player's end-of-match numbers, exactly as the replay's stats event holds them.</summary>
internal sealed record FortniteStatsBlock(
    int Eliminations, int Assists, double Accuracy, int DamageToPlayers, int DamageTaken, int DamageToStructures,
    int MaterialsGathered, int MaterialsUsed, double TravelledCentimetres, int Revives, double Seconds);

/// <summary>Everything decoded from one replay, before it is turned into a <see cref="MatchStats"/>.</summary>
internal sealed record FortniteReplayData(
    FortniteReplayHeader Header,
    FortniteStatsBlock? Stats,
    int? Placement,
    int? TotalPlayers,
    IReadOnlyList<FortniteElimination> Eliminations,
    string? Playlist,
    int SkippedEvents)
{
    public IReadOnlyList<MatchCountStep> PlayersLeft { get; init; } = [];
    public bool PlayersLeftComplete { get; init; }
    public bool ReplayDetailsIncomplete { get; init; }
}

// Decodes a Fortnite replay with FortniteReplayReader, as cheaply as it can.
//
// Two passes, both on a trimmed copy of the file (FortniteReplayFile):
//  1. Events only: the replay's stats, team placement and every elimination.
//     About 10 ms and 2 MB for a 20 MB replay, and it decodes on every
//     current-season replay tested.
//  2. GameState only: playlist and the recorded human/bot counters, timed
//     with each demo frame's replay clock. Unrelated actor data is skipped.
//     Payload bounds are checked before the library copies them: malformed
//     actor blocks in real replays otherwise allocated nearly 2 GB at once.
// Landing positions, weapon identities and pickups require additional actor
// fields that this bounded GameState pass does not decode.
internal static class FortniteReplayParser
{
    internal static readonly TimeSpan PlaylistBudget = TimeSpan.FromSeconds(2);
    private const long PlaylistAllocationBudget = 128L * 1024 * 1024;

    public static FortniteReplayData Parse(FortniteReplayHeader header, bool readPlaylist)
    {
        if (!header.IsReady) throw new InvalidDataException("Replay is not finalised or has no usable encryption key.");
        using var stream = FortniteReplayFile.OpenShared(header.Path);
        FortniteReplayReader.Models.FortniteReplay replay;
        int skipped;
        using (var events = FortniteReplayFile.ReadForParsing(stream, dataChunks: 0, includeEvents: true))
        {
            var reader = new TolerantEventReader();
            replay = reader.ReadReplay(events);
            skipped = reader.SkippedEvents;
        }

        string? playlist = null;
        IReadOnlyList<MatchCountStep> playersLeft = [];
        var playersLeftComplete = false;
        var detailsIncomplete = skipped > 0;
        if (readPlaylist)
        {
            try
            {
                using var network = FortniteReplayFile.ReadForParsing(stream, dataChunks: int.MaxValue, includeEvents: false, out var truncated);
                var probe = new GameStateReader(PlaylistBudget, PlaylistAllocationBudget, header.LengthSeconds);
                playersLeftComplete = probe.Read(network) && !truncated;
                playlist = probe.Playlist;
                playersLeft = playersLeftComplete ? TrimJoiningPlayers(probe.PlayersLeft) : [];
                detailsIncomplete |= probe.Incomplete || !playersLeftComplete;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                detailsIncomplete = true;
                AppLog.Info($"Fortnite replay: playlist not read ({error.GetType().Name}).");
            }
        }

        FortniteStatsBlock? stats = null;
        if (replay.Stats is { } s)
        {
            try
            {
                if (!float.IsFinite(s.Accuracy) || s.Accuracy is < 0 or > 1)
                    throw new InvalidDataException("Invalid replay accuracy.");
                stats = new FortniteStatsBlock(checked((int)s.Eliminations), checked((int)s.Assists), s.Accuracy,
                    checked((int)s.DamageToPlayers), checked((int)s.DamageTaken), checked((int)s.DamageToStructures),
                    checked((int)s.MaterialsGathered), checked((int)s.MaterialsUsed), s.TotalTraveled, checked((int)s.Revives),
                    (s.Info?.StartTime ?? 0) / 1000.0);
            }
            catch (Exception error) when (error is OverflowException or InvalidDataException) { detailsIncomplete = true; }
        }
        var eliminations = replay.Eliminations
            .Where(e => e?.Info is not null)
            .Select(e => new FortniteElimination(e.Info.StartTime / 1000.0, e.Eliminator, e.Eliminated, e.Knocked, e.EliminatedInfo?.IsBot == true))
            .OrderBy(e => e.Seconds)
            .ToArray();
        var team = replay.TeamStats;
        return new FortniteReplayData(header, stats,
            team is { Position: > 0 and <= int.MaxValue } ? (int)team.Position : null,
            team is { TotalPlayers: > 0 and <= int.MaxValue } ? (int)team.TotalPlayers : null,
            eliminations, playlist, skipped)
        {
            PlayersLeft = playersLeft,
            PlayersLeftComplete = playersLeftComplete,
            ReplayDetailsIncomplete = detailsIncomplete
        };
    }

    // The first few seconds contain players joining the bus. Begin at the
    // recorded peak, without inventing a count at replay second zero.
    internal static IReadOnlyList<MatchCountStep> TrimJoiningPlayers(IReadOnlyList<MatchCountStep> samples)
    {
        if (samples.Count == 0) return [];
        var peak = samples.Max(sample => sample.Count);
        var first = 0;
        while (samples[first].Count != peak) first++;
        return samples.Skip(first).ToArray();
    }

    // A malformed event (new season, new layout) costs that one event, not
    // the whole replay: the chunk reader seeks past whatever was left unread.
    private sealed class TolerantEventReader() : ReplayReader(NullLogger.Instance, ParseMode.EventsOnly)
    {
        public int SkippedEvents { get; private set; }

        public override void ReadEvent(FArchive archive)
        {
            try { base.ReadEvent(archive); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                SkippedEvents++;
                if (SkippedEvents == 1) AppLog.Info($"Fortnite replay: skipped an unreadable event ({error.GetType().Name}).");
            }
        }
    }

    // The library catches some exceptions inside packet processing. A budget
    // stop stays set so the next frame/packet unwinds the outer replay loop.
    internal sealed class GameStateReader(TimeSpan budget, long allocationBudget, double lengthSeconds)
        : ReplayReader(NullLogger.Instance, ParseMode.Minimal)
    {
        private sealed class StopException : Exception;

        private readonly long _deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        private readonly long _allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        private bool _stopped;
        private double _seconds;
        private int? _humans, _bots;
        public string? Playlist { get; private set; }
        public bool Incomplete { get; private set; }
        public List<MatchCountStep> PlayersLeft { get; } = [];

        public bool Read(Stream stream)
        {
            try { ReadReplay(stream); return true; }
            catch (StopException) { Incomplete = true; return false; }
        }

        private void Guard()
        {
            if (_stopped || Environment.TickCount64 > _deadline ||
                GC.GetAllocatedBytesForCurrentThread() - _allocatedAtStart > allocationBudget)
                Stop();
        }

        private void Stop() { _stopped = true; throw new StopException(); }

        protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
        {
            Guard();
            if (exportGroup is PlaylistInfo { Name.Length: > 0 } playlist)
            {
                Playlist ??= playlist.Name;
            }
            if (exportGroup is not GameState state) return;
            _humans = state.PlayersLeft ?? _humans;
            _bots = state.PlayerBotsLeft ?? _bots;
            // Current BR replay fields count humans and bots separately.
            // Both are required; absent bot data does not mean zero bots.
            if (_humans is not (>= 0 and <= 200) || _bots is not (>= 0 and <= 200) ||
                _humans + _bots is not (>= 1 and <= 200)) return;
            var step = new MatchCountStep(_seconds, _humans.Value + _bots.Value);
            if (PlayersLeft.Count > 0 && PlayersLeft[^1].Seconds == _seconds) PlayersLeft[^1] = step;
            else if (PlayersLeft.Count == 0 || PlayersLeft[^1].Count != step.Count) PlayersLeft.Add(step);
        }

        protected override void OnExternalDataRead(uint channelIndex, IExternalData? externalData) { }
        protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update) { }
        protected override void OnChannelOpened(uint channelIndex, NetworkGUID? actor) { }
        protected override void OnChannelClosed(uint channelIndex, NetworkGUID? actor) { }

        public override bool ReceivedReplicatorBunch(DataBunch bunch, FBitArchive archive, uint? repObject, bool bHasRepLayout)
        {
            Guard();
            var group = _netGuidCache.GetNetFieldExportGroup(repObject);
            return group?.PathName.Contains("Athena_GameState", StringComparison.Ordinal) == true &&
                base.ReceivedReplicatorBunch(bunch, archive, repObject, bHasRepLayout);
        }

        public override bool ReceiveCustomDeltaProperty(FBitArchive reader, NetFieldExportGroup group, uint channelIndex, bool enablePropertyChecksum) => false;

        public override void ReadDemoFrameIntoPlaybackPackets(FArchive archive)
        {
            Guard();
            var at = archive.Position;
            if (archive.NetworkVersion >= NetworkVersionHistory.HISTORY_MULTIPLE_LEVELS) archive.ReadInt32();
            var seconds = archive.ReadSingle();
            archive.Seek(at);
            if (!float.IsFinite(seconds) || seconds < _seconds || seconds > lengthSeconds + 1) Stop();
            _seconds = seconds;
            base.ReadDemoFrameIntoPlaybackPackets(archive);
        }

        protected override FArchive Decompress(FArchive archive)
        {
            Guard();
            if (Replay.Info.IsCompressed)
            {
                var at = archive.Position;
                var decompressed = archive.ReadInt32();
                var compressed = archive.ReadInt32();
                archive.Seek(at);
                if (decompressed is < 0 or > 8 * 1024 * 1024 || compressed is < 0 or > 8 * 1024 * 1024) Stop();
            }
            return base.Decompress(archive);
        }

        public override PacketState ReadPacket(FArchive archive) { Guard(); return base.ReadPacket(archive); }

        public override uint? ReadContentBlockPayload(DataBunch bunch, out bool bObjectDeleted, out bool bOutHasRepLayout, out uint? payload)
        {
            Guard();
            var result = base.ReadContentBlockPayload(bunch, out bObjectDeleted, out bOutHasRepLayout, out payload);
            if (payload is { } size && size > bunch.Archive.GetBitsLeft())
            {
                Incomplete = true;
                bunch.Archive.SetError();
                payload = null;
            }
            return result;
        }

        public override bool ReadFieldHeaderAndPayload(FBitArchive archive, NetFieldExportGroup group, out NetFieldExport? outField, [NotNullWhen(true)] out uint? payload)
        {
            Guard();
            var result = base.ReadFieldHeaderAndPayload(archive, group, out outField, out payload);
            if (payload is { } size && size > archive.GetBitsLeft())
            {
                Incomplete = true;
                archive.SetError();
                payload = null;
                return false;
            }
            return result;
        }
    }
}
