using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Eve.App.Services;

// Streams one audio track of a clip in 30s chunks extracted on demand -
// YouTube-style progressive loading - instead of extracting the entire
// track to a WAV before playback can start. For a long clip (a 44min Full
// Session x 3 tracks) the old full-file extraction meant minutes of ffmpeg
// and gigabytes of WAV before the editor made a sound; chunk 0 extracts in
// well under a second, so audio is ready essentially immediately, and the
// rest fills in as playback approaches it (each chunk prefetches the next).
//
// Read() is called on the WASAPI render thread, so it never blocks on
// extraction: a chunk that isn't on disk yet plays as silence (position
// keeps advancing, preserving A/V sync) and the real audio takes over the
// moment its file lands. Chunk files live in the same preview-audio cache
// folder (and 7-day prune) as the old full-file WAVs did, keyed by the same
// content-derived cache key, so they're reused across editor opens.
public sealed class ChunkedAudioReader : ISampleProvider, IDisposable
{
    private const int ChunkSeconds = 30;
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const long ChunkFrames = (long)ChunkSeconds * SampleRate;

    // Extraction dedupe/throttle shared across all readers (a clip has one
    // reader per audio track, and RebuildAudioOutput recreates readers) so
    // the same chunk is never extracted twice concurrently and at most two
    // ffmpeg extractions run at once.
    private static readonly ConcurrentDictionary<string, Task> InFlightExtractions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ExtractionGate = new(2, 2);

    private readonly string _inputPath;
    private readonly int _streamIndex;
    private readonly string _cacheDir;
    private readonly string _cacheKey;
    private readonly long _totalFrames;

    private readonly object _readerLock = new();
    private WaveFileReader? _openChunkReader;
    private ISampleProvider? _openChunkSamples;
    private int _openChunkIndex = -1;
    private long _positionFrames;
    private bool _disposed;

    public ChunkedAudioReader(string inputPath, int streamIndex, TimeSpan duration, string cacheDir, string cacheKey)
    {
        _inputPath = inputPath;
        _streamIndex = streamIndex;
        _cacheDir = cacheDir;
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

    // Kicks off extraction of the chunk containing `time` plus the next one,
    // without blocking.
    public void Prefetch(TimeSpan time)
    {
        var chunkIndex = (int)(Math.Max(0, time.TotalSeconds) / ChunkSeconds);
        ScheduleExtraction(chunkIndex);
        ScheduleExtraction(chunkIndex + 1);
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
                    // True EOF - returning less than requested (or 0) tells the
                    // mixer this input is done.
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
                    ScheduleExtraction(chunkIndex);
                    ScheduleExtraction(chunkIndex + 1);
                    var silentSamples = (int)(framesWanted * Channels);
                    Array.Clear(buffer, offset + written, silentSamples);
                    written += silentSamples;
                    _positionFrames += framesWanted;
                    continue;
                }

                var read = _openChunkSamples!.Read(buffer, offset + written, (int)(framesWanted * Channels));
                if (read <= 0)
                {
                    // The chunk file ran short of its nominal 30s (always true
                    // for the final chunk, possible for others by a few frames
                    // of rounding) - pad the remainder of the nominal window
                    // with silence so position stays chunk-aligned.
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
                    ScheduleExtraction(chunkIndex + 1);
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

    private string ChunkPath(int chunkIndex) => Path.Combine(_cacheDir, $"{_cacheKey}-c{chunkIndex:0000}.wav");

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

        var path = ChunkPath(chunkIndex);
        if (!File.Exists(path)) return false;

        try
        {
            CloseOpenChunk();
            var reader = new WaveFileReader(path);
            var byteOffset = frameWithinChunk * reader.WaveFormat.BlockAlign;
            if (byteOffset < reader.Length)
            {
                reader.Position = byteOffset;
            }
            else
            {
                reader.Position = reader.Length;
            }

            _openChunkReader = reader;
            _openChunkSamples = reader.ToSampleProvider();
            _openChunkIndex = chunkIndex;
            return true;
        }
        catch (Exception error)
        {
            // Partially-written/corrupt chunk - drop it and re-extract.
            AppLog.Error($"Editor audio chunk open failed: {path}", error);
            try { File.Delete(path); } catch { }
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

    private void ScheduleExtraction(int chunkIndex)
    {
        if (chunkIndex < 0 || (long)chunkIndex * ChunkFrames >= _totalFrames) return;
        var path = ChunkPath(chunkIndex);
        if (File.Exists(path)) return;

        InFlightExtractions.GetOrAdd(path, _ => Task.Run(async () =>
        {
            await ExtractionGate.WaitAsync();
            try
            {
                if (!File.Exists(path)) await ExtractChunkAsync(chunkIndex, path);
            }
            finally
            {
                ExtractionGate.Release();
                InFlightExtractions.TryRemove(path, out Task? _);
            }
        }));
    }

    private async Task ExtractChunkAsync(int chunkIndex, string outputPath)
    {
        var pendingPath = Path.Combine(_cacheDir, $"{Guid.NewGuid():N}.tmp.wav");
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-y",
            "-v", "error",
            "-ss", (chunkIndex * ChunkSeconds).ToString(),
            "-t", ChunkSeconds.ToString(),
            "-i", _inputPath,
            "-map", $"0:{_streamIndex}",
            "-vn",
            "-sn",
            "-ac", Channels.ToString(),
            "-ar", SampleRate.ToString(),
            "-c:a", "pcm_s16le",
            pendingPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
            var errorTask = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                AppLog.Error($"Editor audio chunk extract failed: input={_inputPath}, stream={_streamIndex}, chunk={chunkIndex}: {(await errorTask).Trim()}");
                try { File.Delete(pendingPath); } catch { }
                return;
            }

            File.Move(pendingPath, outputPath, overwrite: true);
        }
        catch (Exception error)
        {
            AppLog.Error($"Editor audio chunk extract failed: input={_inputPath}, stream={_streamIndex}, chunk={chunkIndex}", error);
            try { File.Delete(pendingPath); } catch { }
        }
    }
}
