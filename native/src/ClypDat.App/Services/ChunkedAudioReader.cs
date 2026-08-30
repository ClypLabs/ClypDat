using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ClypDat.App.Services;

// Streams one audio track of a clip in 30s chunks extracted on demand -
// YouTube-style progressive loading - instead of extracting the entire
// track to a WAV before playback can start. For a long clip (a 44min Full
// Session x 3 tracks) the old full-file extraction meant minutes of ffmpeg
// and gigabytes of WAV before the editor made a sound; chunk 0 extracts in
// well under a second, so audio is ready essentially immediately, and the
// rest fills in as playback approaches it (each chunk prefetches the next).
//
// Read() is called on the WASAPI render thread, so it never blocks on
// extraction: a chunk that isn't ready yet plays as silence (position keeps
// advancing, preserving A/V sync) and the real audio takes over the moment
// it lands. Chunks live in AudioChunkCache - an in-memory, budget-capped
// cache shared process-wide - rather than as WAV files on disk; see its
// comment for why (measured 9GB on one real install with the old disk
// cache).
public sealed class ChunkedAudioReader : ISampleProvider, IDisposable
{
    // A chunk is 30s of 48kHz stereo float PCM (~11MB). 128MB is an order of magnitude
    // of headroom over any legitimate chunk while still bounding a pathological file.
    private const long MaximumChunkPcmBytes = 128L * 1024 * 1024;

    private const int ChunkSeconds = 30;
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const long ChunkFrames = (long)ChunkSeconds * SampleRate;
    private static readonly WaveFormat ChunkWaveFormat = new(SampleRate, 16, Channels);

    // Extraction dedupe/throttle shared across all readers (a clip has one
    // reader per audio track, and RebuildAudioOutput recreates readers) so
    // the same chunk is never extracted twice concurrently and at most two
    // ffmpeg extractions run at once. Network sources get serialized down to
    // ONE at a time on top of that: concurrent readers seeking around the
    // same file over SMB thrash the share badly enough to starve LibVLC's
    // own video stream (a clip that plays fine in standalone VLC - one
    // patient reader - broke in the editor's many-reader setup).
    private static readonly ConcurrentDictionary<string, Task> InFlightExtractions = new(StringComparer.OrdinalIgnoreCase);
    // Two was chosen against a network share, where the constraint is the
    // share and not the CPU. Locally the constraint is the CPU, and a clip
    // with three audio tracks needs three chunks before it can make a sound -
    // at two, the third track always waited out a full extraction it had no
    // reason to.
    private static readonly SemaphoreSlim ExtractionGate =
        new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4), Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    private static readonly SemaphoreSlim NetworkExtractionGate = new(1, 1);

    // Count of chunks somebody is currently WAITING to hear, as opposed to
    // chunks being fetched ahead of playback. Lookahead work parks until this
    // hits zero.
    //
    // Without this the gate was strictly first-come-first-served, and the
    // arrival order is set by construction: each track's reader schedules its
    // own chunk 0 and then its chunk 1 before the next track's reader is even
    // built. So track 2's chunk 0 queued behind track 1's chunk 1 - audio
    // nobody needed for another 30 seconds - and track 3 behind both. The log
    // shows exactly that shape on every editor open: track 1 never starves,
    // track 2 sits silent ~1.3s, track 3 ~2.9s.
    private static int _pendingPriority;

    private readonly string _inputPath;
    private readonly int _streamIndex;
    private readonly string _cacheKey;
    private readonly long _totalFrames;

    private readonly object _readerLock = new();
    private RawSourceWaveStream? _openChunkReader;
    private ISampleProvider? _openChunkSamples;
    private int _openChunkIndex = -1;
    private long _positionFrames;
    private long _starvedFrames;
    private bool _disposed;

    public ChunkedAudioReader(string inputPath, int streamIndex, TimeSpan duration, string cacheKey)
    {
        _inputPath = inputPath;
        _streamIndex = streamIndex;
        _cacheKey = cacheKey;
        // Duration can transiently be unknown while a clip is still being
        // probed - better to keep producing (silent) audio and let the video
        // player's own end-of-media stop playback than to declare EOF at 0s.
        var effectiveDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromHours(12);
        _totalFrames = (long)(effectiveDuration.TotalSeconds * SampleRate);
        Prefetch(TimeSpan.Zero);
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public TimeSpan TotalTime => TimeSpan.FromSeconds(_totalFrames / (double)SampleRate);

    public bool AtEnd => Volatile.Read(ref _positionFrames) >= _totalFrames;

    public TimeSpan CurrentTime
    {
        get => TimeSpan.FromSeconds(Volatile.Read(ref _positionFrames) / (double)SampleRate);
        set
        {
            var frames = Math.Max(0, (long)(value.TotalSeconds * SampleRate));
            lock (_readerLock)
            {
                _positionFrames = frames;
                // Force re-open at the new position on the next Read.
                CloseOpenChunk();
            }

            Prefetch(value);
        }
    }

    // Kicks off extraction of the chunk containing `time`, the next one, and
    // the previous one (small backward seeks - stepping back, re-listening to
    // a moment - are as common as forward playback), without blocking.
    public void Prefetch(TimeSpan time)
    {
        var chunkIndex = (int)(Math.Max(0, time.TotalSeconds) / ChunkSeconds);
        ScheduleExtraction(chunkIndex, priority: true);
        ScheduleExtraction(chunkIndex + 1, priority: false);
        ScheduleExtraction(chunkIndex - 1, priority: false);
    }

    // A waiter never owns extraction cancellation: multiple seeks/readers can
    // share this flight, so cancelling one seek must not starve another.
    internal async Task<bool> EnsureReadyAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        var chunkIndex = (int)(Math.Max(0, time.TotalSeconds) / ChunkSeconds);
        if (AudioChunkCache.Contains(_cacheKey, chunkIndex)) return true;
        await ScheduleExtraction(chunkIndex, priority: true).WaitAsync(cancellationToken).ConfigureAwait(false);
        return AudioChunkCache.Contains(_cacheKey, chunkIndex);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_readerLock)
        {
            if (_disposed) return 0;

            var written = 0;
            while (written < count)
            {
                if (_positionFrames >= _totalFrames)
                {
                    // Past the end of the track: pad the rest of the buffer with
                    // silence and keep the position advancing, rather than
                    // returning a short read.
                    //
                    // A short read is how an ISampleProvider tells
                    // MixingSampleProvider it is finished, and the mixer's
                    // response is to DROP that input permanently. Doing that at
                    // the end of every clip is what killed audio on replay: the
                    // readers were still there, rewinding them still worked, but
                    // nothing was connected to the output any more, so the clip
                    // played back silent until the whole audio graph happened to
                    // be rebuilt. Nothing here depends on audio signalling the
                    // end anyway - playback stops on the video player's own
                    // EndReached (see PlaybackSession).
                    Array.Clear(buffer, offset + written, count - written);
                    _positionFrames += (count - written) / Channels;
                    written = count;
                    break;
                }

                var chunkIndex = (int)(_positionFrames / ChunkFrames);
                var frameWithinChunk = _positionFrames - chunkIndex * ChunkFrames;
                var framesLeftInChunk = ChunkFrames - frameWithinChunk;
                var framesWanted = Math.Min(Math.Min(framesLeftInChunk, _totalFrames - _positionFrames), (count - written) / Channels);
                if (framesWanted <= 0) break;

                if (!TryOpenChunk(chunkIndex, frameWithinChunk))
                {
                    // Chunk not extracted yet - emit silence for this stretch so
                    // the timeline keeps moving in sync with the video, and make
                    // sure the chunk (and the next) is on its way.
                    // This one is being waited on right now, by definition.
                    ScheduleExtraction(chunkIndex, priority: true);
                    ScheduleExtraction(chunkIndex + 1, priority: false);
                    var silentSamples = (int)(framesWanted * Channels);
                    Array.Clear(buffer, offset + written, silentSamples);
                    written += silentSamples;
                    _positionFrames += framesWanted;
                    _starvedFrames += framesWanted;
                    continue;
                }

                // Starvation diagnostic: how much silence the listener just sat
                // through waiting on this chunk - the key number for judging
                // network-drive audio behavior from the log.
                if (_starvedFrames > SampleRate / 5)
                {
                    AppLog.Info($"Editor audio starved: stream={_streamIndex}, chunk={chunkIndex}, silentMs={_starvedFrames * 1000 / SampleRate}, network={PlaybackSession.IsNetworkPath(_inputPath)}.");
                }
                _starvedFrames = 0;

                var read = _openChunkSamples!.Read(buffer, offset + written, (int)(framesWanted * Channels));
                if (read <= 0)
                {
                    // The chunk ran short of its nominal 30s (always true for
                    // the final chunk, possible for others by a few frames of
                    // rounding) - pad the remainder of the nominal window with
                    // silence so position stays chunk-aligned.
                    var padSamples = (int)(framesWanted * Channels);
                    Array.Clear(buffer, offset + written, padSamples);
                    written += padSamples;
                    _positionFrames += framesWanted;
                    continue;
                }

                written += read;
                _positionFrames += read / Channels;

                // Approaching this chunk's tail - make sure the next one is
                // ready before playback gets there.
                if (frameWithinChunk + read / Channels > ChunkFrames * 3 / 4)
                {
                    ScheduleExtraction(chunkIndex + 1, priority: false);
                }
            }

            return written;
        }
    }

    public void Dispose()
    {
        lock (_readerLock)
        {
            _disposed = true;
            CloseOpenChunk();
        }
    }

    private bool TryOpenChunk(int chunkIndex, long frameWithinChunk)
    {
        if (_openChunkIndex == chunkIndex && _openChunkSamples is not null)
        {
            // Reader position can drift from _positionFrames after a seek
            // within the same chunk - CurrentTime's CloseOpenChunk() handles
            // that by forcing a reopen, so an open reader here is always
            // already at the right spot.
            return true;
        }

        if (!AudioChunkCache.TryGet(_cacheKey, chunkIndex, out var pcm)) return false;

        try
        {
            CloseOpenChunk();
            // MemoryStream(byte[]) wraps the array directly (no copy) - the
            // cache entry stays the single copy of this chunk's data for as
            // long as it's resident.
            var stream = new RawSourceWaveStream(new MemoryStream(pcm, writable: false), ChunkWaveFormat);
            var byteOffset = frameWithinChunk * ChunkWaveFormat.BlockAlign;
            stream.Position = Math.Min(byteOffset, stream.Length);

            _openChunkReader = stream;
            _openChunkSamples = stream.ToSampleProvider();
            _openChunkIndex = chunkIndex;
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"Editor audio chunk open failed: cacheKey={_cacheKey}, chunk={chunkIndex}", error);
            AudioChunkCache.Remove(_cacheKey, chunkIndex);
            CloseOpenChunk();
            return false;
        }
    }

    private void CloseOpenChunk()
    {
        _openChunkReader?.Dispose();
        _openChunkReader = null;
        _openChunkSamples = null;
        _openChunkIndex = -1;
    }

    private Task ScheduleExtraction(int chunkIndex, bool priority)
    {
        if (chunkIndex < 0 || (long)chunkIndex * ChunkFrames >= _totalFrames) return Task.CompletedTask;
        if (AudioChunkCache.Contains(_cacheKey, chunkIndex)) return Task.CompletedTask;

        var isNetwork = PlaybackSession.IsNetworkPath(_inputPath);
        var flightKey = $"{_cacheKey}-c{chunkIndex:0000}";
        return InFlightExtractions.GetOrAdd(flightKey, _ =>
        {
            if (priority) Interlocked.Increment(ref _pendingPriority);
            return Task.Run(async () =>
            {
                try
                {
                    // Lookahead yields to anything being waited on. Priority
                    // work always reaches the decrement below, so this cannot
                    // park forever.
                    while (!priority && Volatile.Read(ref _pendingPriority) > 0)
                    {
                        await Task.Delay(20).ConfigureAwait(false);
                    }

                    var queueClock = Stopwatch.StartNew();
                    await ExtractionGate.WaitAsync().ConfigureAwait(false);
                    var networkEntered = false;
                    try
                    {
                        if (isNetwork)
                        {
                            await NetworkExtractionGate.WaitAsync().ConfigureAwait(false);
                            networkEntered = true;
                        }
                        if (!AudioChunkCache.Contains(_cacheKey, chunkIndex)) await ExtractChunkAsync(chunkIndex);
                    }
                    finally
                    {
                        if (networkEntered) NetworkExtractionGate.Release();
                        ExtractionGate.Release();
                    }
                }
                finally
                {
                    if (priority) Interlocked.Decrement(ref _pendingPriority);
                    InFlightExtractions.TryRemove(flightKey, out Task? _);
                }
            });
        });
    }

    private async Task ExtractChunkAsync(int chunkIndex)
    {
        var startInfo = new ProcessStartInfo(FfmpegPathResolver.FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = FfmpegPathResolver.WorkingDirectory,
        };
        foreach (var argument in new[]
        {
            "-y",
            "-v", "error",
            // See MediaProbeService.ReadWaveformAsync - one audio stream is
            // not worth a thread per core, and several of these run at once.
            "-threads", "1",
            "-ss", (chunkIndex * ChunkSeconds).ToString(),
            "-t", ChunkSeconds.ToString(),
            "-i", _inputPath,
            "-map", $"0:{_streamIndex}",
            "-vn",
            "-sn",
            "-ac", Channels.ToString(),
            "-ar", SampleRate.ToString(),
            "-f", "s16le",
            "-c:a", "pcm_s16le",
            "-"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var clock = Stopwatch.StartNew();
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
            // Both pipes drained concurrently while the process runs - reading
            // stdout to EOF (CopyToAsync) waits on ffmpeg to finish writing and
            // close it, which happens at/just before exit, so this is safe to
            // await before WaitForExitAsync rather than deadlocking on a full
            // pipe buffer.
            var errorTask = process.StandardError.ReadToEndAsync();
            // Bounded copy. The input is capped with -t, but a file with pathological or
            // non-monotonic timestamps can make ffmpeg emit far more PCM than that
            // implies, and this buffers all of it before ToArray doubles it again.
            using var pcm = new MemoryStream();
            var pcmBuffer = new byte[81920];
            int pcmRead;
            var truncated = false;
            using var timeoutCts = new CancellationTokenSource(PlaybackSession.IsNetworkPath(_inputPath) ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(10));
            try
            {
                while ((pcmRead = await process.StandardOutput.BaseStream.ReadAsync(pcmBuffer, timeoutCts.Token)) > 0)
                {
                    if (pcm.Length + pcmRead > MaximumChunkPcmBytes)
                    {
                        truncated = true;
                        try { process.Kill(entireProcessTree: true); } catch { }
                        break;
                    }

                    pcm.Write(pcmBuffer, 0, pcmRead);
                }
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                AppLog.Info($"Editor audio chunk extract timeout: stream={_streamIndex}, chunk={chunkIndex}, timeoutMs={(PlaybackSession.IsNetworkPath(_inputPath) ? 30000 : 10000)}, network={PlaybackSession.IsNetworkPath(_inputPath)}.");
                return;
            }

            if (truncated)
            {
                AppLog.Error($"Editor audio chunk exceeded {MaximumChunkPcmBytes / (1024 * 1024)}MB: input={_inputPath}, stream={_streamIndex}, chunk={chunkIndex}.");
                return;
            }

            await process.WaitForExitAsync();
            var error = await errorTask;

            if (process.ExitCode != 0 || pcm.Length == 0)
            {
                AppLog.Error($"Editor audio chunk extract failed: input={_inputPath}, stream={_streamIndex}, chunk={chunkIndex}: {error.Trim()}");
                return;
            }

            AudioChunkCache.Store(_cacheKey, chunkIndex, pcm.ToArray());
            // Per-chunk extraction time is the primary network-drive health
            // metric: local NVMe lands well under 200ms; a share that takes
            // multiple seconds per chunk is what audio starvation reports
            // trace back to.
            AppLog.Debug($"Editor audio chunk extracted: stream={_streamIndex}, chunk={chunkIndex}, ms={clock.ElapsedMilliseconds}, bytes={pcm.Length}, network={PlaybackSession.IsNetworkPath(_inputPath)}.");
        }
        catch (Exception error)
        {
            AppLog.Error($"Editor audio chunk extract failed: input={_inputPath}, stream={_streamIndex}, chunk={chunkIndex}", error);
        }
    }
}

// Global, process-lifetime, memory-bounded cache of extracted PCM chunks -
// replaces the old on-disk WAV chunk cache (measured 9GB on one real install
// with no cleanup, later capped to a 7-day prune) with an in-memory LRU
// instead: no permanent disk footprint, and no time-based staleness to
// reason about - a chunk that stops being touched just falls out under
// eviction pressure from newer ones, whenever that happens to occur.
//
// Bounded by design, not just in practice: Store() always runs an eviction
// pass immediately after adding, so resident bytes can only ever exceed
// BudgetBytes by whatever a handful of concurrent extractions (capped by
// ChunkedAudioReader's ExtractionGate, at most 2 at once) add between one
// eviction pass and the next - never allowed to grow unbounded the way the
// old disk cache's per-file WAVs did. Entries hold only raw PCM byte[] data,
// nothing that chains back to a ViewModel/session/UI object, so they can
// never keep heavier objects alive past their own use.
internal static class AudioChunkCache
{
    // ~5.76MB per 30s chunk at 48kHz/16-bit/stereo. Budget covers a few
    // minutes of lookback/lookahead across several tracks at once (a Full
    // Session can have 3-4) without letting a long scrub session's resident
    // set grow without limit.
    // Halved from 256MB. That was sized as "a few minutes of lookback across
    // several tracks", but a quarter of a gigabyte of decoded PCM is a large
    // share of the app's whole footprint, and the chunks it was buying are
    // ones playback had already passed - at 128MB a three-track clip still
    // keeps ~3.5 minutes resident per track, which is far more scrubbing
    // headroom than anyone uses before the extractor can refill it.
    private const long BudgetBytes = 128L * 1024 * 1024;

    private sealed class Entry(byte[] data, long lastUsedTicks)
    {
        public readonly byte[] Data = data;
        public long LastUsedTicks = lastUsedTicks;
    }

    private static readonly ConcurrentDictionary<(string CacheKey, int ChunkIndex), Entry> Entries = new();
    private static long _totalBytes;
    private static readonly object EvictionLock = new();

    public static bool TryGet(string cacheKey, int chunkIndex, out byte[] data)
    {
        if (Entries.TryGetValue((cacheKey, chunkIndex), out var entry))
        {
            Volatile.Write(ref entry.LastUsedTicks, Stopwatch.GetTimestamp());
            data = entry.Data;
            return true;
        }

        data = Array.Empty<byte>();
        return false;
    }

    public static bool Contains(string cacheKey, int chunkIndex) => Entries.ContainsKey((cacheKey, chunkIndex));

    public static void Store(string cacheKey, int chunkIndex, byte[] data)
    {
        var entry = new Entry(data, Stopwatch.GetTimestamp());
        if (!Entries.TryAdd((cacheKey, chunkIndex), entry)) return;

        Interlocked.Add(ref _totalBytes, data.LongLength);
        EvictIfNeeded();
    }

    // Drops every resident chunk. Called by MemoryTrimmer when the editor is
    // closed - nothing is playing, so every byte in here is lookahead for a
    // clip nobody is watching, and at ~5.76MB per chunk this is the single
    // largest thing the app holds that it does not currently need. Re-extracts
    // on demand the next time a clip is opened.
    public static void Clear()
    {
        foreach (var key in Entries.Keys)
        {
            Remove(key.CacheKey, key.ChunkIndex);
        }
    }

    public static void Remove(string cacheKey, int chunkIndex)
    {
        if (Entries.TryRemove((cacheKey, chunkIndex), out var entry))
        {
            Interlocked.Add(ref _totalBytes, -entry.Data.LongLength);
        }
    }

    // Linear scan for the least-recently-used entry - the byte budget keeps
    // the resident set small enough in practice (well under 100 chunks) that
    // this is cheaper to reason about than maintaining a proper LRU list, and
    // it only runs when a new chunk actually pushes the total over budget.
    private static void EvictIfNeeded()
    {
        lock (EvictionLock)
        {
            while (Interlocked.Read(ref _totalBytes) > BudgetBytes)
            {
                (string CacheKey, int ChunkIndex)? oldestKey = null;
                var oldestTicks = long.MaxValue;
                foreach (var pair in Entries)
                {
                    var ticks = Volatile.Read(ref pair.Value.LastUsedTicks);
                    if (ticks < oldestTicks)
                    {
                        oldestTicks = ticks;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey is null) return;
                Remove(oldestKey.Value.CacheKey, oldestKey.Value.ChunkIndex);
            }
        }
    }
}
