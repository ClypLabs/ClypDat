using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClypDat.App.Services;

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
            "ClypDat",
            "media-cache");
        Directory.CreateDirectory(_cacheFolder);
        // Delayed, not fired immediately: this constructor runs during cold
        // boot alongside the library cache load, thumbnail File.Exists
        // checks, and the disk/network scan in RefreshLibraryAsync - all
        // real work the user is waiting on. A full EnumerateFiles+stat sweep
        // of this folder (which only grows over months of use) competing
        // with those for the same disk right at the coldest moment is
        // exactly the kind of "everything at once" contention that shows up
        // as a brief system-wide stutter. The prune only cares about
        // entries untouched for 30 days, so a few seconds' delay costs
        // nothing.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            PruneStaleCache();
        });
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

    // Returns FileInfo, not paths: the directory enumeration already carries
    // each entry's size and timestamps, so handing those along means the
    // ordering below and CreateLibraryStub after it read what's already in
    // hand instead of issuing a fresh stat per clip each. That was two extra
    // disk round-trips per clip on a path that runs for the whole library at
    // startup, when the disk is at its coldest.
    public IEnumerable<FileInfo> EnumerateVideos(string folderPath)
    {
        return new DirectoryInfo(folderPath)
            .EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(file => IsVideoFile(file.FullName))
            .OrderByDescending(file => file.CreationTimeUtc);
    }

    public MediaFileInfo CreateLibraryStub(string filePath) => CreateLibraryStub(new FileInfo(filePath));

    public MediaFileInfo CreateLibraryStub(FileInfo info)
    {
        var filePath = info.FullName;
        var thumbnailPath = GetThumbnailPath(filePath);
        var thumbnail = new FileInfo(thumbnailPath);
        // Recency marker for PruneStaleCache - see its comment. Only rewritten
        // once it's actually getting old: this runs for every clip on every
        // library refresh, and a metadata WRITE per clip is far more expensive
        // than the read beside it, especially on a cold disk at boot. The
        // prune cutoff is 30 days, so a day's resolution is plenty.
        if (thumbnail.Exists && DateTime.UtcNow - thumbnail.LastWriteTimeUtc > TimeSpan.FromDays(1))
        {
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
            thumbnail.Exists ? thumbnailPath : string.Empty,
            cached?.Tracks ?? Array.Empty<MediaTrackInfo>(),
            cached?.Width ?? 0,
            cached?.Height ?? 0,
            cached?.Fps ?? 0,
            cached?.CaptureBackend ?? string.Empty,
            File.Exists(filmstripPath) ? filmstripPath : string.Empty,
            info.LastWriteTimeUtc,
            cached is null || cached.Tracks.Any(track => track.Type == "video"));
    }

    public async Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return (await ProbeDurationAsync(filePath, cancellationToken).ConfigureAwait(false)).Duration;
    }

    public async Task<MediaDurationProbeResult> ProbeDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var result = await RunProcessAsync("ffprobe", new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=nw=1:nk=1",
            filePath
        }, cancellationToken).ConfigureAwait(false);
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
        var media = await ProbeMetadataAsync(filePath).ConfigureAwait(false);
        if (!media.HasVideo) return media;
        var thumbnailPath = await EnsureThumbnailAsync(filePath, media.Duration).ConfigureAwait(false);
        var filmstripPath = await EnsureFilmstripAsync(filePath, media.Duration).ConfigureAwait(false);
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
                File.Exists(filmstripPath) ? filmstripPath : string.Empty,
                info.LastWriteTimeUtc,
                cached.Tracks.Any(track => track.Type == "video"));
        }

        var result = await RunProcessAsync("ffprobe", new[]
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            filePath
        }).ConfigureAwait(false);

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
            File.Exists(filmstripPath) ? filmstripPath : string.Empty,
            info.LastWriteTimeUtc,
            tracks.Any(track => track.Type == "video"));

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
                media.Tracks,
                media.HasVideo);
            File.WriteAllText(GetProbeCachePath(filePath), JsonSerializer.Serialize(entry));
        }
        catch
        {
            // Probe cache is a pure speedup - losing an entry just means the next load re-probes.
        }
    }

    // An audio-only MP4 has a valid duration and audio tracks, but no frame
    // exists for ffmpeg to write. Probe metadata is already cached before any
    // visual work begins, so this check prevents a failed thumbnail/filmstrip
    // job from being retried every launch.
    private bool HasCachedVideoStream(string filePath)
    {
        var info = new FileInfo(filePath);
        var cached = TryReadProbeCache(filePath, info);
        return cached is null || cached.Tracks.Any(track => track.Type == "video");
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
        var cached = await TryReadWaveformCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
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
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
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
                wholeWaveforms[track.Index] = await ReadWaveformAsync(media.Path, track.Index, null, null, cancellationToken).ConfigureAwait(false);
                onPartial?.Invoke(track.Index, wholeWaveforms[track.Index]);
            }

            await TryWriteWaveformCacheAsync(cachePath, wholeWaveforms, cancellationToken).ConfigureAwait(false);
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

        // Segment boundaries up front, with a runt tail folded into the
        // segment before it. A 61s clip used to produce a 60s segment plus a
        // 1s one, and every segment costs an ffmpeg process PER TRACK - three
        // extra decodes to draw one second of waveform.
        var segments = new List<(double Start, double Length)>();
        var cursor = 0.0;
        while (cursor < totalSeconds)
        {
            var length = Math.Min(SegmentSeconds, totalSeconds - cursor);
            if (totalSeconds - cursor - length < SegmentSeconds / 2) length = totalSeconds - cursor;
            segments.Add((cursor, length));
            cursor += length;
        }

        var decodeClock = System.Diagnostics.Stopwatch.StartNew();
        long firstSegmentMs = -1;
        for (var segment = 0; segment < segments.Count; segment++)
        {
            var (segmentStart, segmentLength) = segments[segment];
            var startBucket = (int)(segmentStart / totalSeconds * BucketCount);
            var endBucket = segment == segments.Count - 1 ? BucketCount : (int)((segmentStart + segmentLength) / totalSeconds * BucketCount);
            if (endBucket <= startBucket) continue;

            cancellationToken.ThrowIfCancellationRequested();
            // The tracks of one segment are independent decodes of independent
            // streams, and they ran strictly one after another - on a clip with
            // Game/Chat/Mic that is three sequential ffmpeg processes to fill a
            // single slice of the timeline, on a machine with cores to spare.
            // Segments stay sequential so the waveform still paints
            // left-to-right as it arrives.
            var segmentResults = await Task.WhenAll(audioTracks.Select(track =>
                ReadWaveformAsync(media.Path, track.Index, segmentStart, segmentLength, cancellationToken))).ConfigureAwait(false);

            for (var index = 0; index < audioTracks.Length; index++)
            {
                var track = audioTracks[index];
                var segmentPeaks = segmentResults[index];
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
        AppLog.Debug($"Waveform decoded: segments={segments.Count}x{audioTracks.Length}tracks, firstSegmentMs={firstSegmentMs}, totalMs={decodeClock.ElapsedMilliseconds}, path={media.Path}");

        var waveforms = peaksByTrack.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<double>)pair.Value);
        await TryWriteWaveformCacheAsync(cachePath, waveforms, cancellationToken).ConfigureAwait(false);
        return waveforms;
    }

    // Rename/move keeps the same media content under a new path - reuse the
    // existing probe/thumbnail/filmstrip/waveform artifacts under the new
    // path's cache key instead of deleting and forcing ffprobe/ffmpeg to
    // regenerate everything, which is expensive on a network drive. Mirrors
    // LibraryLayout.MoveSidecars's same move-don't-recreate approach for
    // sidecar files.
    public void MoveCacheFor(string oldFilePath, string newFilePath)
    {
        var oldKey = CacheKey(oldFilePath);
        var newKey = CacheKey(newFilePath);
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return;

        foreach (var oldPath in Directory.EnumerateFiles(_cacheFolder, $"{oldKey}*.*"))
        {
            var newPath = Path.Combine(_cacheFolder, newKey + Path.GetFileName(oldPath)[oldKey.Length..]);
            if (File.Exists(newPath)) TryDelete(oldPath);
            else TryMove(oldPath, newPath);
        }

        var oldFrameFolder = Path.Combine(_cacheFolder, $"{oldKey}-frames");
        var newFrameFolder = Path.Combine(_cacheFolder, $"{newKey}-frames");
        if (Directory.Exists(oldFrameFolder))
        {
            if (Directory.Exists(newFrameFolder))
            {
                try { Directory.Delete(oldFrameFolder, true); } catch { /* best-effort */ }
            }
            else
            {
                try { Directory.Move(oldFrameFolder, newFrameFolder); } catch { /* best-effort - falls back to regeneration */ }
            }
        }
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

        // Filmstrip is a single flat file ({key}-filmstrip-v2.jpg), already
        // caught by the {key}*.* glob above - no folder to separately clean up.

        TryDelete(GetWaveformPath(filePath));
        // The disk copy is gone; the in-memory decoded bitmaps keyed by those
        // same paths are not, and would keep being served for the rest of the
        // session (a trim-save regenerates the thumbnail at the SAME path).
        BitmapCache.Invalidate(GetThumbnailPath(filePath));
        BitmapCache.Invalidate(GetFilmstripPath(filePath));
    }

    public async Task<string> EnsureThumbnailAsync(string filePath, TimeSpan duration)
    {
        var output = GetThumbnailPath(filePath);
        if (File.Exists(output))
        {
            return output;
        }
        if (!HasCachedVideoStream(filePath)) return string.Empty;

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
            }).ConfigureAwait(false);

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

    // Re-grabs the cached library-card thumbnail at an explicit timestamp,
    // overwriting the same cache path EnsureThumbnailAsync uses - called when
    // the user moves a clip's TrimStart handle, so the card representing it
    // shows the frame the clip now actually opens on instead of a stale one
    // from before the trim. Unlike EnsureThumbnailAsync, there's no
    // black-frame retry chain here: the user explicitly chose this exact
    // position, so it's shown as-is even if it happens to be black.
    public async Task<string> RegenerateThumbnailAsync(string filePath, TimeSpan atTime)
    {
        var output = GetThumbnailPath(filePath);
        var result = await RunProcessAsync("ffmpeg", new[]
        {
            "-y",
            "-ss", Math.Max(0, atTime.TotalSeconds).ToString("0.###"),
            "-i", filePath,
            "-frames:v", "1",
            // Same -2 rounding as EnsureThumbnailAsync - an odd height from
            // preserving aspect exactly fails the JPEG encoder's 4:2:0 output.
            "-vf", "scale=960:-2",
            "-q:v", "4",
            output
        }).ConfigureAwait(false);

        if (result.ExitCode != 0 || !File.Exists(output))
        {
            AppLog.Error($"Thumbnail regeneration failed for {filePath}: {(string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg failed" : result.Error.Trim())}");
            return string.Empty;
        }

        // Same path, new content - the decoded copy in memory is now stale.
        BitmapCache.Invalidate(output);
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
    // Holds the actual generation job, not a lock: a second request for the
    // same clip now SHARES the in-flight job instead of queueing behind it and
    // then finding the file already there. The job itself runs uncancellable
    // (callers observe it through their own token via WaitAsync below) so one
    // caller giving up - the common case, a superseded editor open - doesn't
    // throw away work that every other caller still wants, only to re-run the
    // whole thing on the next open. The dictionary entry is removed on
    // completion; the old per-path SemaphoreSlim map was never pruned and
    // retained one semaphore per clip for the life of the process.
    private static readonly ConcurrentDictionary<string, Task<string>> FilmstripJobs = new(StringComparer.OrdinalIgnoreCase);

    // Every frame still gets its OWN -ss seek (fast keyframe seek, only decodes
    // a handful of frames around the target) - NOT a single `fps`/`select`
    // filter pass across the whole file, which would force ffmpeg to decode
    // EVERY frame start to end just to pick out a sparse few, pegging CPU for
    // the clip's whole duration. Same reasoning as EnsureThumbnailAsync's -ss.
    //
    // The difference from before: those ten seeks are ten seeked INPUTS to one
    // ffmpeg invocation, hstack'd into the strip in the same pass, instead of
    // ten separate processes writing temp JPEGs plus an eleventh `tile` process
    // to combine them. Eleven Process.Start calls became one, and the temp
    // directory (and its recursive delete) is gone entirely, along with ten
    // pointless JPEG encode/decode round-trips. Both mattered a lot more than
    // expected because this used to run on the UI thread - see the Task.Run in
    // MainWindowViewModel.StartFilmstripLoad.
    public Task<string> EnsureFilmstripAsync(string filePath, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var output = GetFilmstripPath(filePath);
        if (File.Exists(output)) return Task.FromResult(output);
        if (duration <= TimeSpan.Zero) return Task.FromResult(string.Empty);
        if (!HasCachedVideoStream(filePath)) return Task.FromResult(string.Empty);

        var job = FilmstripJobs.GetOrAdd(output, key =>
        {
            var started = GenerateFilmstripAsync(filePath, duration, output);
            // Detach the cleanup from the returned task so a caller cancelling
            // out never leaves a completed job cached as if still in flight.
            _ = started.ContinueWith(
                _ => FilmstripJobs.TryRemove(key, out Task<string>? _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return started;
        });

        // The shared job keeps running if this particular caller walks away.
        return job.WaitAsync(cancellationToken);
    }

    private static async Task<string> GenerateFilmstripAsync(string filePath, TimeSpan duration, string output)
    {
        try
        {
            var arguments = new List<string> { "-y", "-v", "error" };
            for (var i = 0; i < FilmstripFrameCount; i++)
            {
                var seek = (i + 0.5) / FilmstripFrameCount * duration.TotalSeconds;
                arguments.Add("-ss");
                arguments.Add(seek.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                arguments.Add("-i");
                arguments.Add(filePath);
            }

            // scale=-2 (not -1): preserving aspect exactly can produce an ODD
            // width, which the JPEG encoder's 4:2:0 output rejects outright -
            // same trap EnsureThumbnailAsync documents for height.
            var filter = new System.Text.StringBuilder();
            for (var i = 0; i < FilmstripFrameCount; i++)
            {
                filter.Append($"[{i}:v]scale=-2:{FilmstripFrameHeight}[f{i}];");
            }
            for (var i = 0; i < FilmstripFrameCount; i++)
            {
                filter.Append($"[f{i}]");
            }
            filter.Append($"hstack=inputs={FilmstripFrameCount}[strip]");

            arguments.AddRange(new[]
            {
                "-filter_complex", filter.ToString(),
                "-map", "[strip]",
                "-frames:v", "1",
                "-update", "1",
                "-an",
                "-q:v", "2",
                output
            });

            var result = await RunProcessAsync("ffmpeg", arguments.ToArray()).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(output))
            {
                AppLog.Error($"Filmstrip generation failed for {filePath}: {(string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg failed" : result.Error.Trim())}");
                return string.Empty;
            }

            return output;
        }
        catch (Exception error)
        {
            AppLog.Error($"Filmstrip generation failed for {filePath}", error);
            return string.Empty;
        }
    }

    private string GetFilmstripPath(string filePath)
    {
        // -v2: the strip is now built by one hstack pass instead of ten frame
        // grabs plus a tile pass. The old key carried no version at all, and
        // TimelineLaneControl slices the sheet by the CURRENT
        // FilmstripFrameCount - so without a bump, any change to how the sheet
        // is laid out would silently mis-slice every already-cached strip.
        return Path.Combine(_cacheFolder, $"{CacheKey(filePath)}-filmstrip-v2.jpg");
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
            var data = await JsonSerializer.DeserializeAsync<Dictionary<int, double[]>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            await JsonSerializer.SerializeAsync(stream, serializable, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        var tempPath = Path.Combine(Path.GetTempPath(), $"clypdat-waveform-{Guid.NewGuid():N}.f32");
        try
        {
            // Single-threaded on purpose: decoding one audio stream down to
            // 4kHz mono is a trivial amount of CPU, and several of these now
            // run at once. Letting each one spin up a thread per core just
            // adds scheduling overhead to work that was never parallel.
            var args = new List<string> { "-y", "-v", "error", "-threads", "1" };
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
            var result = await RunProcessAsync("ffmpeg", args.ToArray(), cancellationToken).ConfigureAwait(false);

            return result.ExitCode == 0 && File.Exists(tempPath)
                ? BuildPeaks(await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false), 700)
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

    // Nothing capped the total number of probe processes before: the idle
    // filmstrip sweep (HydrateMissingFilmstripsAsync) walks the WHOLE library,
    // and FilmstripLocks only ever deduped two jobs on the same clip, so a big
    // library could have as many ffmpeg processes in flight as it had clips -
    // all competing with the editor's own decode. Two at a time, matching what
    // ChunkedAudioReader.ExtractionGate and AudioCapturePipeline.FfmpegGate
    // already do for their own ffmpeg work.
    private static readonly SemaphoreSlim ProbeProcessGate = new(2, 2);

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        await ProbeProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunProcessCoreAsync(fileName, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ProbeProcessGate.Release();
        }
    }

    private static async Task<ProcessResult> RunProcessCoreAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
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
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        return new ProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
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

    private static void TryMove(string oldPath, string newPath)
    {
        try
        {
            File.Move(oldPath, newPath);
        }
        catch
        {
            // Best effort - a failed move just falls back to regeneration.
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
    string FilmstripPath = "",
    DateTime LastWriteTimeUtc = default,
    bool HasVideo = true);

public sealed record MediaTrackInfo(int Index, string Type, string Codec, string Label, double VolumePercent = 100);

internal sealed record ProbeCacheEntry(
    TimeSpan Duration,
    long SizeBytes,
    long LastWriteTimeUtcTicks,
    int Width,
    int Height,
    double Fps,
    string CaptureBackend,
    IReadOnlyList<MediaTrackInfo> Tracks,
    bool HasVideo = true);

public sealed record MediaDurationProbeResult(TimeSpan Duration, string Error);

internal sealed record SteelSeriesAudioTrack(string Name, double Volume, bool Muted);

internal sealed record ProcessResult(int ExitCode, string Output, string Error);
