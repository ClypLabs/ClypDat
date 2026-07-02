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

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
        {
            using var doc = JsonDocument.Parse(result.Output);
            if (doc.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationJson) &&
                double.TryParse(durationJson.GetString(), out var seconds))
            {
                duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            }

            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = GetString(stream, "codec_type");
                    var codecName = GetString(stream, "codec_name");
                    var index = GetInt(stream, "index");
                    var label = BuildTrackLabel(stream, codecType, index);

                    if (codecType == "video")
                    {
                        width = Math.Max(width, GetInt(stream, "width"));
                        height = Math.Max(height, GetInt(stream, "height"));
                        fps = Math.Max(fps, ParseRate(GetString(stream, "avg_frame_rate")));
                    }

                    if (codecType is "video" or "audio" or "subtitle")
                    {
                        tracks.Add(new MediaTrackInfo(index, codecType, codecName, label));
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
            fps);
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

    public async Task<IReadOnlyList<string>> EnsurePreviewFramesAsync(MediaFileInfo media)
    {
        var folder = Path.Combine(_cacheFolder, $"{CacheKey(media.Path)}-frames");
        Directory.CreateDirectory(folder);

        const int frameCount = 60;
        var existing = Directory.EnumerateFiles(folder, "*.jpg").OrderBy(path => path).ToArray();
        if (existing.Length >= frameCount)
        {
            return existing;
        }

        var duration = Math.Max(1, media.Duration.TotalSeconds);
        for (var i = 0; i < frameCount; i++)
        {
            var output = Path.Combine(folder, $"{i:D2}.jpg");
            if (File.Exists(output)) continue;

            var seek = Math.Min(duration - 0.1, Math.Max(0, duration * i / frameCount));
            await RunProcessAsync("ffmpeg", new[]
            {
                "-y",
                "-ss", seek.ToString("0.###"),
                "-i", media.Path,
                "-frames:v", "1",
                "-vf", "scale=480:-1",
                "-q:v", "5",
                output
            });
        }

        return Directory.EnumerateFiles(folder, "*.jpg").OrderBy(path => path).ToArray();
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
            "-vf", "scale=960:-1",
            "-q:v", "4",
            output
        });

        return result.ExitCode == 0 && File.Exists(output) ? output : string.Empty;
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
            var title = GetString(tags, "title");
            if (!string.IsNullOrWhiteSpace(title)) return title;

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
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
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
    double Fps);

public sealed record MediaTrackInfo(int Index, string Type, string Codec, string Label);

internal sealed record ProcessResult(int ExitCode, string Output, string Error);
