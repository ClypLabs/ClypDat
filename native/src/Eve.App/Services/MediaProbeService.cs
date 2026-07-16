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
        _cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "thumbnails");
        Directory.CreateDirectory(_cacheFolder);
        Task.Run(PruneStaleCache);
    }

    // Thumbnails (and cached probe metadata) are keyed by clip path+size, so
    // entries for deleted/moved clips can never be hit again yet stayed on
    // disk forever. Same retention approach as the preview-audio cache: sweep
    // anything not used in 30 days; CreateLibraryStub bumps LastWriteTime on
    // every hit, so clips still in the library always stay fresh (a library
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
        return new MediaFileInfo(
            Path.GetFileNameWithoutExtension(filePath),
            filePath,
            info.CreationTimeUtc,
            TimeSpan.Zero,
            info.Length,
            File.Exists(thumbnailPath) ? thumbnailPath : string.Empty,
            Array.Empty<MediaTrackInfo>(),
            0,
            0,
            0);
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

    public async Task<MediaFileInfo> ProbeAsync(string filePath)
    {
        var info = new FileInfo(filePath);
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

        var thumbnailPath = await EnsureThumbnailAsync(filePath, duration);

        return new MediaFileInfo(
            Path.GetFileNameWithoutExtension(filePath),
            filePath,
            info.CreationTimeUtc,
            duration,
            info.Length,
            thumbnailPath,
            tracks,
            width,
            height,
            fps,
            captureBackend);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<double>>> LoadWaveformsAsync(
        MediaFileInfo media,
        CancellationToken cancellationToken)
    {
        var audioTracks = media.Tracks.Where(track => track.Type == "audio").ToArray();
        if (audioTracks.Length == 0) return new Dictionary<int, IReadOnlyList<double>>();

        var cachePath = GetWaveformPath(media.Path);
        var cached = await TryReadWaveformCacheAsync(cachePath, cancellationToken);
        if (cached.Count > 0) return cached;

        var waveforms = new Dictionary<int, IReadOnlyList<double>>();
        foreach (var track in audioTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            waveforms[track.Index] = await ReadWaveformAsync(media.Path, track.Index, cancellationToken);
        }

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

        TryDelete(GetWaveformPath(filePath));
    }

    private async Task<string> EnsureThumbnailAsync(string filePath, TimeSpan duration)
    {
        var output = GetThumbnailPath(filePath);
        if (File.Exists(output))
        {
            return output;
        }

        var seek = duration > TimeSpan.FromSeconds(2)
            ? Math.Min(3, duration.TotalSeconds / 3)
            : 0;

        var result = await RunProcessAsync("ffmpeg", new[]
        {
            "-y",
            "-ss", seek.ToString("0.###"),
            "-i", filePath,
            "-frames:v", "1",
            // -2, not -1: -1 preserves aspect exactly, which can produce an
            // ODD height (e.g. ultrawide 3440x1440 -> 960x403), and the JPEG
            // encoder's 4:2:0 output needs even dimensions - ffmpeg then fails
            // and the clip silently never got a thumbnail. -2 preserves aspect
            // rounded to the nearest even value. 1080p sources never hit this
            // (960x540), which is why it looked resolution-dependent.
            "-vf", "scale=960:-2",
            "-q:v", "4",
            output
        });

        if (result.ExitCode != 0 || !File.Exists(output))
        {
            AppLog.Error($"Thumbnail generation failed for {filePath}: {(string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg failed" : result.Error.Trim())}");
            return string.Empty;
        }

        return output;
    }

    private string GetThumbnailPath(string filePath)
    {
        return Path.Combine(_cacheFolder, $"{CacheKey(filePath)}.jpg");
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

    private static async Task<IReadOnlyList<double>> ReadWaveformAsync(
        string filePath,
        int streamIndex,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eve-waveform-{Guid.NewGuid():N}.f32");
        try
        {
            var result = await RunProcessAsync("ffmpeg", new[]
            {
                "-y",
                "-v", "error",
                "-i", filePath,
                "-map", $"0:{streamIndex}",
                "-vn",
                "-sn",
                "-ac", "1",
                "-ar", "4000",
                "-f", "f32le",
                tempPath
            }, cancellationToken);

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

            peaks[bucket] = Math.Clamp(max, 0.02, 1);
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
    string CaptureBackend = "");

public sealed record MediaTrackInfo(int Index, string Type, string Codec, string Label, double VolumePercent = 100);

public sealed record MediaDurationProbeResult(TimeSpan Duration, string Error);

internal sealed record SteelSeriesAudioTrack(string Name, double Volume, bool Muted);

internal sealed record ProcessResult(int ExitCode, string Output, string Error);
