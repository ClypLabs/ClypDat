using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Eve.App.Services;

public sealed class MediaProbeService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv"
    };

    private readonly string _cacheFolder;

    public MediaProbeService()
    {
        // Named "thumbnails" originally, back when that's all it held - now
        // also holds waveform peaks and probed metadata (duration/tracks/
        // resolution), so "media-cache" is the honest name going forward.
        // Old "thumbnails" folders from prior versions are just left behind;
        // it's disposable cache data, not worth a migration.
        _cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "media-cache");
        Directory.CreateDirectory(_cacheFolder);
        Task.Run(PruneStaleCache);
    }

    // Thumbnails (and cached probe metadata) are keyed by clip path+size, so
    // entries for deleted/moved clips can never be hit again yet stayed on
    // disk forever. Sweep anything not used in 30 days; CreateLibraryStub
    // bumps LastWriteTime on every hit, so clips still in the library
    // always stay fresh (a library
    // refresh touches every visible clip's thumbnail). Regeneration on a
    // wrongly-evicted entry is just one ffmpeg frame grab, so a stale sweep
    // is cheap to be wrong about.
    private void PruneStaleCache()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            foreach (var file in Directory.EnumerateFiles(_cacheFolder))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    try { File.Delete(file); } catch { /* best effort */ }
                }
            }
        }
        catch
        {
            // Cache pruning must never block startup.
        }
    }

    public static bool IsVideoFile(string path)
    {
        return VideoExtensions.Contains(Path.GetExtension(path));
    }

    public IEnumerable<string> EnumerateVideos(string folderPath)
    {
        return Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(IsVideoFile)
            .OrderByDescending(File.GetCreationTimeUtc);
    }

    public MediaFileInfo CreateLibraryStub(string filePath)
    {
        var info = new FileInfo(filePath);
        var thumbnailPath = GetThumbnailPath(filePath);
        if (File.Exists(thumbnailPath))
        {
            // Recency marker for PruneStaleCache - see its comment.
            try { File.SetLastWriteTimeUtc(thumbnailPath, DateTime.UtcNow); } catch { }
        }

        // Read-only check (no ffmpeg) - filmstrip generation itself only
        // happens in ProbeAsync (during hydration), same split as
        // ThumbnailPath above.
        var filmstripPath = GetFilmstripPath(filePath);

        // A cached probe (see ProbeAsync/WriteProbeCache) means an unchanged
        // file's duration/tracks/etc can paint on the very first frame - no
        // waiting on HydrateLibraryClipsAsync to reach this clip's turn.
        // Without this, EVERY library load showed 0:00 on every card until
        // hydration (one ffprobe at a time) worked its way down the list,
        // even for a library the user had already opened before.
        var cached = TryReadProbeCache(filePath, info);
        return new MediaFileInfo(
            Path.GetFileNameWithoutExtension(filePath),
            filePath,
            info.CreationTimeUtc,
            cached?.Duration ?? TimeSpan.Zero,
            info.Length,
            File.Exists(thumbnailPath) ? thumbnailPath : string.Empty,
            cached?.Tracks ?? Array.Empty<MediaTrackInfo>(),
            cached?.Width ?? 0,
            cached?.Height ?? 0,
            cached?.Fps ?? 0,
            cached?.CaptureBackend ?? string.Empty,
            File.Exists(filmstripPath) ? filmstripPath : string.Empty);
    }

    public async Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return (await ProbeDurationAsync(filePath, cancellationToken)).Duration;
    }

    public async Task<MediaDurationProbeResult> ProbeDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var result = await RunProcessAsync("ffprobe", new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=nw=1:nk=1",
            filePath
        }, cancellationToken);
        if (result.ExitCode == 0 && double.TryParse(result.Output.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return new MediaDurationProbeResult(TimeSpan.FromSeconds(seconds), string.Empty);
        }

        return new MediaDurationProbeResult(TimeSpan.Zero, string.IsNullOrWhiteSpace(result.Error) ? "ffprobe could not read a duration." : result.Error.Trim());
    }

    // Full probe: metadata (from cache if possible) AND generates the
    // thumbnail/filmstrip if either is missing. Used where a single specific
    // clip's complete info is needed right away (opening a clip, adding one
    // new clip to the library) - for hydrating the WHOLE library, see
    // ProbeMetadataAsync/EnsureThumbnailAsync/EnsureFilmstripAsync instead,
    // called as three separate passes (MainWindowViewModel.
    // HydrateLibraryClipsAsync) so a single clip's full pipeline can't block
    // every other clip behind it in the list from getting at least its basic
    // info quickly.
    public async Task<MediaFileInfo> ProbeAsync(string filePath)
    {
        var media = await ProbeMetadataAsync(filePath);
        var thumbnailPath = await EnsureThumbnailAsync(filePath, media.Duration);
        var filmstripPath = await EnsureFilmstripAsync(filePath, media.Duration);
        return media with { ThumbnailPath = thumbnailPath, FilmstripPath = filmstripPath };
    }

    // Metadata only (duration/tracks/resolution/etc) - no ffmpeg thumbnail/
    // filmstrip generation, just whichever of those already happen to exist
    // in cache (a cheap File.Exists check, same as CreateLibraryStub). This
    // is the cheap, fast part of a full probe: a cache hit is just a JSON
    // read, and even a real ffprobe call is far lighter than image
    // generation - see HydrateLibraryClipsAsync for why that split matters.
    public async Task<MediaFileInfo> ProbeMetadataAsync(string filePath)
    {
        var info = new FileInfo(filePath);
        var thumbnailPath = GetThumbnailPath(filePath);
        var filmstripPath = GetFilmstripPath(filePath);

        // Cache hit: the file's size+mtime match what was last probed, so
        // its duration/tracks/resolution can't have changed - skip ffprobe
        // entirely instead of re-reading the whole file's stream info on
        // every single library load (the main cost on a network drive).
        var cached = TryReadProbeCache(filePath, info);
        if (cached is not null)
        {
            return new MediaFileInfo(
                Path.GetFileNameWithoutExtension(filePath),
                filePath,
                info.CreationTimeUtc,
                cached.Duration,
                info.Length,
                File.Exists(thumbnailPath) ? thumbnailPath : string.Empty,
                cached.Tracks,
                cached.Width,
                cached.Height,
                cached.Fps,
                cached.CaptureBackend,
                File.Exists(filmstripPath) ? filmstripPath : string.Empty);
        }

        var result = await RunProcessAsync("ffprobe", new[]
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            filePath
        });

        TimeSpan duration = TimeSpan.Zero;
        var tracks = new List<MediaTrackInfo>();
        var width = 0;
        var height = 0;
        var fps = 0d;
        var captureBackend = string.Empty;

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
        {
            using var doc = JsonDocument.Parse(result.Output);
            var steelSeriesAudioTracks = Array.Empty<SteelSeriesAudioTrack>();
            if (doc.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationJson))
            {
                if (double.TryParse(durationJson.GetString(), out var seconds))
                {
                    duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
                }

                steelSeriesAudioTracks = ReadSteelSeriesAudioTracks(format);
                if (format.TryGetProperty("tags", out var formatTags) &&
                    formatTags.TryGetProperty("comment", out var commentTag))
                {
                    var comment = commentTag.GetString() ?? string.Empty;
                    var prefix = ClipMetadataTagger.BackendTagKey + "=";
                    if (comment.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        captureBackend = comment[prefix.Length..];
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                var audioIndex = 0;
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = GetString(stream, "codec_type");
                    var codecName = GetString(stream, "codec_name");
                    var index = GetInt(stream, "index");
                    var audioTrack = codecType == "audio" && audioIndex < steelSeriesAudioTracks.Length
                        ? steelSeriesAudioTracks[audioIndex]
                        : null;
                    var label = audioTrack?.Name ?? BuildTrackLabel(stream, codecType, index);
                    var volumePercent = audioTrack is null
                        ? 100
                        : Math.Clamp(audioTrack.Muted ? 0 : audioTrack.Volume * 100, 0, 150);

                    if (codecType == "video")
                    {
                        width = Math.Max(width, GetInt(stream, "width"));
                        height = Math.Max(height, GetInt(stream, "height"));
                        fps = Math.Max(fps, ParseRate(GetString(stream, "avg_frame_rate")));
                    }

                    if (codecType is "video" or "audio" or "subtitle")
                    {
                        tracks.Add(new MediaTrackInfo(index, codecType, codecName, label, volumePercent));
                    }

                    if (codecType == "audio")
                    {
                        audioIndex++;
                    }
                }
            }
        }

        var media = new MediaFileInfo(
            Path.GetFileNameWithoutExtension(filePath),
            filePath,
            info.CreationTimeUtc,
            duration,
            info.Length,
            File.Exists(thumbnailPath) ? thumbnailPath : string.Empty,
            tracks,
            width,
            height,
            fps,
            captureBackend,
            File.Exists(filmstripPath) ? filmstripPath : string.Empty);

        if (duration > TimeSpan.Zero)
        {
            WriteProbeCache(filePath, info, media);
        }

        return media;
    }

    private ProbeCacheEntry? TryReadProbeCache(string filePath, FileInfo info)
    {
        try
        {
            var path = GetProbeCachePath(filePath);
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<ProbeCacheEntry>(File.ReadAllText(path));
            if (entry is null) return null;
            // Size+mtime instead of a content hash - cheap enough to check on
            // every library load (a stat the OS/SMB client already did to
            // build the FileInfo), while still catching the file having
            // changed since it was last probed.
            if (entry.SizeBytes != info.Length || entry.LastWriteTimeUtcTicks != info.LastWriteTimeUtc.Ticks) return null;
            return entry;
        }
        catch
        {
            return null;
        }
    }

    private void WriteProbeCache(string filePath, FileInfo info, MediaFileInfo media)
    {
        try
        {
            var entry = new ProbeCacheEntry(
                media.Duration,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                media.Width,
                media.Height,
                media.Fps,
                media.CaptureBackend,
                media.Tracks);
            File.WriteAllText(GetProbeCachePath(filePath), JsonSerializer.Serialize(entry));
        }
        catch
        {
            // Probe cache is a pure speedup - losing an entry just means the next load re-probes.
        }
    }

    private string GetProbeCachePath(string filePath)
    {
        return Path.Combine(_cacheFolder, $"{CacheKey(filePath)}-probe.json");
    }

    // onPartial (optional) fires on a background thread after each decoded
    // segment with the peaks-so-far for one stream - undecoded stretches sit
    // at the silence floor and fill in left-to-right, so long clips show a
    // progressively-growing waveform instead of nothing until the whole file
    // has been decoded. Segments are interleaved across tracks so every lane
    // grows together rather than one completing before the next starts.
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<double>>> LoadWaveformsAsync(
        MediaFileInfo media,
        CancellationToken cancellationToken,
        Action<int, IReadOnlyList<double>>? onPartial = null)
    {
        var audioTracks = media.Tracks.Where(track => track.Type == "audio").ToArray();
        if (audioTracks.Length == 0) return new Dictionary<int, IReadOnlyList<double>>();

        var cachePath = GetWaveformPath(media.Path);
        var cached = await TryReadWaveformCacheAsync(cachePath, cancellationToken);
        if (cached.Count > 0) return cached;

        // On a network drive, waveform decoding competes with LibVLC's video
        // stream and the audio chunk extractor for the same remote file the
        // moment a clip opens - SMB seek thrash from three concurrent readers
        // is what made long network clips stutter in the editor while
        // standalone VLC played them fine. Give playback a head start; the
        // waveform is the least urgent of the three. Only applies here, past
        // the cache check above - an already-cached waveform is just a local
        // JSON read and has nothing to contend with, so it used to eat this
        // same 4s delay for no reason even on a clip opened many times before.
        if (PlaybackSession.IsNetworkPath(media.Path))
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
        }

        const int BucketCount = 700;
        const double SegmentSeconds = 60;
        var totalSeconds = media.Duration.TotalSeconds;

        // Unknown duration - can't map segments to bucket ranges, decode whole
        // tracks in one pass like before.
        if (totalSeconds <= 1)
        {
            var wholeWaveforms = new Dictionary<int, IReadOnlyList<double>>();
            foreach (var track in audioTracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                wholeWaveforms[track.Index] = await ReadWaveformAsync(media.Path, track.Index, null, null, cancellationToken);
                onPartial?.Invoke(track.Index, wholeWaveforms[track.Index]);
            }

            await TryWriteWaveformCacheAsync(cachePath, wholeWaveforms, cancellationToken);
            return wholeWaveforms;
        }

        var peaksByTrack = audioTracks.ToDictionary(
            track => track.Index,
            _ =>
            {
                var peaks = new double[BucketCount];
                Array.Fill(peaks, 0.02);
                return peaks;
            });

        var segmentCount = (int)Math.Ceiling(totalSeconds / SegmentSeconds);
        var decodeClock = System.Diagnostics.Stopwatch.StartNew();
        long firstSegmentMs = -1;
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var segmentStart = segment * SegmentSeconds;
            var segmentLength = Math.Min(SegmentSeconds, totalSeconds - segmentStart);
            var startBucket = (int)(segmentStart / totalSeconds * BucketCount);
            var endBucket = segment == segmentCount - 1 ? BucketCount : (int)((segmentStart + segmentLength) / totalSeconds * BucketCount);
            if (endBucket <= startBucket) continue;

            foreach (var track in audioTracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segmentPeaks = await ReadWaveformAsync(media.Path, track.Index, segmentStart, segmentLength, cancellationToken);
                var target = peaksByTrack[track.Index];
                for (var bucket = startBucket; bucket < endBucket; bucket++)
                {
                    // Resample the segment's own peak list onto this segment's
                    // slice of the full-clip bucket range.
                    var source = (int)((bucket - startBucket) / (double)(endBucket - startBucket) * segmentPeaks.Count);
                    target[bucket] = segmentPeaks[Math.Min(segmentPeaks.Count - 1, source)];
                }

                onPartial?.Invoke(track.Index, target.ToArray());
            }

            if (firstSegmentMs < 0) firstSegmentMs = decodeClock.ElapsedMilliseconds;
        }

        // Network-drive diagnostic: first-segment latency is what the user
        // perceives (when the waveform starts appearing); total/segment count
        // shows whether the share's throughput is the bottleneck.
        AppLog.Debug($"Waveform decoded: segments={segmentCount}x{audioTracks.Length}tracks, firstSegmentMs={firstSegmentMs}, totalMs={decodeClock.ElapsedMilliseconds}, path={media.Path}");

        var waveforms = peaksByTrack.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<double>)pair.Value);
        await TryWriteWaveformCacheAsync(cachePath, waveforms, cancellationToken);
        return waveforms;
    }

    public void DeleteCacheFor(string filePath)
    {
        var key = CacheKey(filePath);
        foreach (var file in Directory.EnumerateFiles(_cacheFolder, $"{key}*.*"))
        {
            TryDelete(file);
        }

        var frameFolder = Path.Combine(_cacheFolder, $"{key}-frames");
        if (Directory.Exists(frameFolder))
        {
            Directory.Delete(frameFolder, true);
        }

        // Filmstrip is a single flat file ({key}-filmstrip-v3.jpg), already
        // caught by the {key}*.* glob above - no folder to separately clean up.

        TryDelete(GetWaveformPath(filePath));
    }

    public async Task<string> EnsureThumbnailAsync(string filePath, TimeSpan duration)
    {
        var output = GetThumbnailPath(filePath);
        if (File.Exists(output))
        {
            return output;
        }

        var seek = duration > TimeSpan.FromSeconds(2)
            ? Math.Min(3, duration.TotalSeconds / 3)
            : 0;

        // Recordings made before the "skip recording black frames" capture
        // fix (NativeReplayBuffer.CaptureLoop) can still have a real black
        // stretch baked into the file's opening - sometimes MINUTES, if the
        // user alt-tabbed away right after the game was detected and didn't
        // switch back for a while (one real Full Session measured black for
        // the first ~260s of an ~524s recording). New recordings never hit
        // this. Fractions of the clip's own duration (not fixed offsets, which
        // could never reach far enough into a long clip's black stretch) keep
        // this scaling to any length, and each is still just one cheap
        // single-frame grab - not the full blackdetect scan that used to live
        // here and was the main network-drive slowdown.
        var candidateSeeks = new List<double> { seek };
        foreach (var fraction in new[] { 0.10, 0.25, 0.50, 0.75 })
        {
            var candidate = duration.TotalSeconds * fraction;
            if (candidate > seek + 0.5 && candidate < duration.TotalSeconds - 0.5) candidateSeeks.Add(candidate);
        }

        var result = new ProcessResult(-1, string.Empty, string.Empty);
        for (var attempt = 0; attempt < candidateSeeks.Count; attempt++)
        {
            result = await RunProcessAsync("ffmpeg", new[]
            {
                "-y",
                "-ss", candidateSeeks[attempt].ToString("0.###"),
                "-i", filePath,
                "-frames:v", "1",
                // blackframe passes the frame through unchanged, only logs to
                // stderr when it's >=98% black - lets one ffmpeg call both
                // grab AND check the frame instead of a separate scan pass.
                // -2, not -1: -1 preserves aspect exactly, which can produce
                // an ODD height (e.g. ultrawide 3440x1440 -> 960x403), and
                // the JPEG encoder's 4:2:0 output needs even dimensions -
                // ffmpeg then fails and the clip silently never got a
                // thumbnail. -2 preserves aspect rounded to the nearest even
                // value. 1080p sources never hit this (960x540), which is
                // why it looked resolution-dependent.
                "-vf", "blackframe=98:32,scale=960:-2",
                "-q:v", "4",
                output
            });

            if (result.ExitCode != 0 || !File.Exists(output)) continue;

            var isBlack = System.Text.RegularExpressions.Regex.IsMatch(result.Error, @"blackframe.*pblack:");
            if (!isBlack) return output;
        }

        // Every candidate came back black (or ffmpeg failed outright) -
        // either a genuinely all-black clip or a real failure. The last
        // attempt's output (if any) is still the best available guess.
        if (!File.Exists(output))
        {
            AppLog.Error($"Thumbnail generation failed for {filePath}: {(string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg failed" : result.Error.Trim())}");
            return string.Empty;
        }

        AppLog.Info($"Thumbnail seek: every candidate frame looked black, keeping the last one: path={filePath}.");
        return output;
    }

    // Editor timeline's video-lane filmstrip (TimelineLaneControl) - a single
    // cached image holding FilmstripFrameCount frames tiled left-to-right,
    // sampled evenly across the clip, generated once here during hydration
    // (HydrateLibraryClipsAsync/ProbeAsync) rather than lazily when the
    // editor opens, so it's already sitting in cache by the time the user
    // picks a clip. One flat file, not a folder of separate frame images -
    // TimelineLaneControl reads it as a spritesheet at render time (each
    // frame is bitmap.Width/FilmstripFrameCount wide), so it still renders
    // each frame individually cropped to its own on-screen cell without
    // distortion despite being cached as a single image.
    public const int FilmstripFrameCount = 10;
    private const int FilmstripFrameHeight = 160;

    // Each frame is grabbed with its OWN -ss seek (fast keyframe seek, only
    // decodes a handful of frames around the target) - not a single `fps`
    // filter pass across the whole file, which forces ffmpeg to decode
    // EVERY frame from start to end just to pick out a sparse few, pegging
    // CPU for the whole clip's duration regardless of how few frames were
    // actually wanted. Same reasoning as EnsureThumbnailAsync's -ss usage.
    // The combine-into-one-strip pass afterward reads only these small
    // already-extracted JPEGs, not the source video, so it's effectively free.
    public async Task<string> EnsureFilmstripAsync(string filePath, TimeSpan duration)
    {
        var output = GetFilmstripPath(filePath);
        if (File.Exists(output)) return output;
        if (duration <= TimeSpan.Zero) return string.Empty;

        var tempDir = Path.Combine(Path.GetTempPath(), $"eve-filmstrip-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            for (var i = 0; i < FilmstripFrameCount; i++)
            {
                var seek = (i + 0.5) / FilmstripFrameCount * duration.TotalSeconds;
                var framePath = Path.Combine(tempDir, $"f{i:0000}.jpg");
                var frameResult = await RunProcessAsync("ffmpeg", new[]
                {
                    "-y",
                    "-ss", seek.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-i", filePath,
                    "-frames:v", "1",
                    "-vf", $"scale=-2:{FilmstripFrameHeight}",
                    "-q:v", "2",
                    framePath
                });

                if (frameResult.ExitCode != 0 || !File.Exists(framePath))
                {
                    AppLog.Error($"Filmstrip frame grab failed for {filePath} at {seek:0.0}s: {(string.IsNullOrWhiteSpace(frameResult.Error) ? "ffmpeg failed" : frameResult.Error.Trim())}");
                    return string.Empty;
                }
            }

            var combineResult = await RunProcessAsync("ffmpeg", new[]
            {
                "-y",
                "-i", Path.Combine(tempDir, "f%04d.jpg"),
                "-vf", $"tile={FilmstripFrameCount}x1",
                "-frames:v", "1",
                "-update", "1",
                "-q:v", "2",
                output
            });

            if (combineResult.ExitCode != 0 || !File.Exists(output))
            {
                AppLog.Error($"Filmstrip combine failed for {filePath}: {(string.IsNullOrWhiteSpace(combineResult.Error) ? "ffmpeg failed" : combineResult.Error.Trim())}");
                return string.Empty;
            }

            return output;
        }
        catch (Exception error)
        {
            AppLog.Error($"Filmstrip generation failed for {filePath}", error);
            return string.Empty;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private string GetFilmstripPath(string filePath)
    {
        return Path.Combine(_cacheFolder, $"{CacheKey(filePath)}-filmstrip.jpg");
    }

    private string GetThumbnailPath(string filePath)
    {
        // -v3: cache-key suffix bump so thumbnails generated before the
        // blackframe-retry fallback (EnsureThumbnailAsync) regenerate - a
        // clip whose fixed 3s seek landed on real black got that baked into
        // its cached jpg permanently otherwise. Old entries age out via the
        // 30-day prune.
        return Path.Combine(_cacheFolder, $"{CacheKey(filePath)}-v3.jpg");
    }

    private string GetWaveformPath(string filePath)
    {
        return Path.Combine(_cacheFolder, $"{CacheKey(filePath)}-waveforms.json");
    }

    private static async Task<Dictionary<int, IReadOnlyList<double>>> TryReadWaveformCacheAsync(
        string cachePath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(cachePath)) return new Dictionary<int, IReadOnlyList<double>>();
            await using var stream = File.OpenRead(cachePath);
            var data = await JsonSerializer.DeserializeAsync<Dictionary<int, double[]>>(stream, cancellationToken: cancellationToken);
            return data?.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<double>)pair.Value)
                ?? new Dictionary<int, IReadOnlyList<double>>();
        }
        catch
        {
            return new Dictionary<int, IReadOnlyList<double>>();
        }
    }

    private static async Task TryWriteWaveformCacheAsync(
        string cachePath,
        Dictionary<int, IReadOnlyList<double>> waveforms,
        CancellationToken cancellationToken)
    {
        try
        {
            var serializable = waveforms.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            await using var stream = File.Create(cachePath);
            await JsonSerializer.SerializeAsync(stream, serializable, cancellationToken: cancellationToken);
        }
        catch
        {
            // Waveform cache is optional.
        }
    }

    // startSeconds/lengthSeconds null = decode the whole track (unknown-duration
    // fallback); otherwise decode just that window (-ss/-t input options, so
    // ffmpeg byte-seeks instead of decoding from the top) for the segmented
    // progressive load.
    private static async Task<IReadOnlyList<double>> ReadWaveformAsync(
        string filePath,
        int streamIndex,
        double? startSeconds,
        double? lengthSeconds,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eve-waveform-{Guid.NewGuid():N}.f32");
        try
        {
            var args = new List<string> { "-y", "-v", "error" };
            if (startSeconds is not null && lengthSeconds is not null)
            {
                args.AddRange(new[] { "-ss", startSeconds.Value.ToString("0.###"), "-t", lengthSeconds.Value.ToString("0.###") });
            }
            args.AddRange(new[]
            {
                "-i", filePath,
                "-map", $"0:{streamIndex}",
                "-vn",
                "-sn",
                "-ac", "1",
                "-ar", "4000",
                "-f", "f32le",
                tempPath
            });
            var result = await RunProcessAsync("ffmpeg", args.ToArray(), cancellationToken);

            return result.ExitCode == 0 && File.Exists(tempPath)
                ? BuildPeaks(await File.ReadAllBytesAsync(tempPath, cancellationToken), 700)
                : BuildFallbackPeaks(streamIndex, 700);
        }
        catch
        {
            return BuildFallbackPeaks(streamIndex, 700);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static IReadOnlyList<double> BuildPeaks(byte[] bytes, int bucketCount)
    {
        var sampleCount = bytes.Length / sizeof(float);
        if (sampleCount == 0) return BuildFallbackPeaks(0, bucketCount);

        var peaks = new double[bucketCount];
        var samplesPerBucket = Math.Max(1, sampleCount / bucketCount);
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = bucket * samplesPerBucket;
            var end = bucket == bucketCount - 1 ? sampleCount : Math.Min(sampleCount, start + samplesPerBucket);
            var max = 0d;
            for (var sample = start; sample < end; sample++)
            {
                var value = Math.Abs(BitConverter.ToSingle(bytes, sample * sizeof(float)));
                if (value > max) max = value;
            }

            // No artificial floor - true silence (max == 0) should render as a
            // gap in the waveform, not a flat baseline line the whole way through.
            peaks[bucket] = Math.Clamp(max, 0, 1);
        }

        return peaks;
    }

    private static IReadOnlyList<double> BuildFallbackPeaks(int seed, int bucketCount)
    {
        var peaks = new double[bucketCount];
        for (var i = 0; i < peaks.Length; i++)
        {
            var wave = Math.Sin((i + seed) * 0.31) + Math.Sin((i + seed) * 0.083);
            var noise = Math.Abs(Math.Sin((i + 3) * (seed + 2) * 0.017));
            var silent = Math.Sin((i + seed) * 0.12) > 0.76 || Math.Sin((i + seed) * 0.047) < -0.88;
            peaks[i] = silent ? 0.02 : Math.Clamp(((Math.Abs(wave) * 9 + noise * 20) % 30) / 30, 0.02, 1);
        }

        return peaks;
    }

    private static string BuildTrackLabel(JsonElement stream, string codecType, int index)
    {
        var prefix = codecType switch
        {
            "video" => "Video",
            "audio" => "Audio",
            "subtitle" => "Subtitle",
            _ => "Track"
        };

        if (stream.TryGetProperty("tags", out var tags))
        {
            var handlerName = GetString(tags, "handler_name");
            if (!string.IsNullOrWhiteSpace(handlerName) &&
                !handlerName.Equals("VideoHandler", StringComparison.OrdinalIgnoreCase) &&
                !handlerName.Equals("SoundHandler", StringComparison.OrdinalIgnoreCase))
            {
                return handlerName;
            }

            var title = GetString(tags, "title");
            if (!string.IsNullOrWhiteSpace(title) &&
                !title.StartsWith("Track", StringComparison.OrdinalIgnoreCase))
            {
                return title;
            }

            var language = GetString(tags, "language");
            if (!string.IsNullOrWhiteSpace(language)) return $"{prefix} {index} ({language})";
        }

        return $"{prefix} {index}";
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) ? value.ToString() : string.Empty;
    }

    private static int GetInt(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;
    }

    private static double ParseRate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0/0") return 0;
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], out var top) &&
            double.TryParse(parts[1], out var bottom) &&
            bottom > 0)
        {
            return top / bottom;
        }

        return double.TryParse(value, out var number) ? number : 0;
    }

    private static SteelSeriesAudioTrack[] ReadSteelSeriesAudioTracks(JsonElement format)
    {
        if (!format.TryGetProperty("tags", out var tags)) return Array.Empty<SteelSeriesAudioTrack>();

        var json = string.Concat(tags.EnumerateObject()
            .Where(property => property.Name.StartsWith("STEELSERIES_META", StringComparison.OrdinalIgnoreCase))
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .Select(property => property.Value.ToString()));
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SteelSeriesAudioTrack>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("audio_tracks_props", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<SteelSeriesAudioTrack>();
            }

            return tracks.EnumerateArray()
                .Select(track => new SteelSeriesAudioTrack(
                    GetString(track, "name"),
                    GetDouble(track, "volume", 1),
                    GetBool(track, "muted")))
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<SteelSeriesAudioTrack>();
        }
    }

    private static double GetDouble(JsonElement element, string property, double fallback)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number)
            ? number
            : fallback;
    }

    private static bool GetBool(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static string CacheKey(string path)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null) return new ProcessResult(-1, string.Empty, "Failed to start process.");
        try
        {
            // Thumbnail/filmstrip/waveform generation runs in the background
            // (library hydration, or right after a save) and has nowhere
            // near the urgency of the editor's OWN ffmpeg calls (audio chunk
            // extraction, LibVLC decode) - at equal/Normal priority the two
            // compete directly for CPU, which is what made opening a
            // freshly-saved clip (its thumbnail/filmstrip not cached yet,
            // full generation running right as the editor tries to start
            // playback) visibly stutter. BelowNormal still runs at full
            // speed whenever a core is actually free, it only yields under
            // real contention - same approach AudioCapturePipeline already
            // uses for its own background mux/concat processes.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Priority is a nice-to-have; never let it block the probe.
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch
        {
            // Best effort cancellation.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cache cleanup should not block deleting clips.
        }
    }
}

public sealed record MediaFileInfo(
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    TimeSpan Duration,
    long SizeBytes,
    string ThumbnailPath,
    IReadOnlyList<MediaTrackInfo> Tracks,
    int Width,
    int Height,
    double Fps,
    string CaptureBackend = "",
    string FilmstripPath = "");

public sealed record MediaTrackInfo(int Index, string Type, string Codec, string Label, double VolumePercent = 100);

internal sealed record ProbeCacheEntry(
    TimeSpan Duration,
    long SizeBytes,
    long LastWriteTimeUtcTicks,
    int Width,
    int Height,
    double Fps,
    string CaptureBackend,
    IReadOnlyList<MediaTrackInfo> Tracks);

public sealed record MediaDurationProbeResult(TimeSpan Duration, string Error);

internal sealed record SteelSeriesAudioTrack(string Name, double Volume, bool Muted);

internal sealed record ProcessResult(int ExitCode, string Output, string Error);
