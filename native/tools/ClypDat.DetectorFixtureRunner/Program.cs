using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ClypDat.App.Services;

namespace ClypDat.DetectorFixtureRunner;

internal static class Program
{
    private const double FramesPerSecond = 2;
    private static readonly DetectorRegionSet Regions = DetectorRegions.ForGame("helldivers2")!;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine("Usage: ClypDat.DetectorFixtureRunner <recording.mp4> <labels.json> [ffmpeg.exe]");
            return 2;
        }

        var recordingPath = Path.GetFullPath(args[0]);
        var labelsPath = Path.GetFullPath(args[1]);
        var ffmpegPath = args.Length == 3 ? Path.GetFullPath(args[2]) : "ffmpeg";
        if (!File.Exists(recordingPath) || !File.Exists(labelsPath))
        {
            Console.Error.WriteLine("Recording or label file does not exist.");
            return 2;
        }

        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(labelsPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("Label file is empty.");

        var workingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClypDat", "DetectorFixtureCache", fixture.RecordingId + "-counter-v2");
        Directory.CreateDirectory(workingDirectory);
        if (!Directory.EnumerateFiles(workingDirectory, "frame-*.png").Any())
            await ExtractFramesAsync(ffmpegPath, recordingPath, workingDirectory, fixture);
        var detections = await DetectAsync(workingDirectory, fixture);
        return Report(fixture, detections);
    }

    private static async Task ExtractFramesAsync(string ffmpegPath, string recordingPath, string outputDirectory, Fixture fixture)
    {
        var windows = fixture.Labels
            .Select(label => new FixtureWindow(Math.Max(0, label.TimeSeconds - 8), 16))
            .OrderBy(window => window.StartSeconds)
            .ToArray();

        // Streak start and intermediate thresholds must be observed, even when
        // the only positive label is the eventual disappearance.
        if (fixture.Labels.Any(label => label.EventId?.StartsWith("killstreak-", StringComparison.Ordinal) == true))
            windows = [new FixtureWindow(0, fixture.Labels.Max(label => label.TimeSeconds) + 8)];

        foreach (var window in windows)
        {
            var startMilliseconds = (long)Math.Round(window.StartSeconds * 1000);
            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-hwaccel", "auto",
                         "-ss", window.StartSeconds.ToString(CultureInfo.InvariantCulture), "-i", recordingPath,
                         "-t", window.DurationSeconds.ToString(CultureInfo.InvariantCulture),
                         "-vf", $"fps={FramesPerSecond.ToString(CultureInfo.InvariantCulture)}",
                         "-start_number", "0",
                         Path.Combine(outputDirectory, $"frame-{startMilliseconds:D9}-%04d.png")
                     })
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start FFmpeg.");
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg frame extraction failed: {error.Trim()}");
        }
    }

    private static async Task<IReadOnlyList<Helldivers2DetectedEvent>> DetectAsync(string workingDirectory, Fixture fixture)
    {
        var reader = new WindowsOcrFrameReader();
        var counterReader = new Helldivers2CounterReader(reader.ReadTextAsync);
        var detector = new Helldivers2Detector();
        var detections = new List<Helldivers2DetectedEvent>();
        var frames = Directory.EnumerateFiles(workingDirectory, "frame-*.png").Select(path =>
        {
            var parts = Path.GetFileNameWithoutExtension(path).Split('-');
            var startMilliseconds = long.Parse(parts[1], CultureInfo.InvariantCulture);
            var frameIndex = int.Parse(parts[2], CultureInfo.InvariantCulture);
            return new FrameSample(path, TimeSpan.FromMilliseconds(startMilliseconds + frameIndex * 1000 / FramesPerSecond));
        }).OrderBy(frame => frame.Timestamp).DistinctBy(frame => frame.Timestamp).ToArray();
        TimeSpan? previousTimestamp = null;
        for (var index = 0; index < frames.Length; index++)
        {
            var frame = frames[index];
            if (previousTimestamp is { } previous && frame.Timestamp - previous > TimeSpan.FromSeconds(2)) detector.ResetSession();
            previousTimestamp = frame.Timestamp;
            var full = GrayPng.Read(frame.Path);
            var center = await reader.ReadTextAsync(GrayTemplateMatcher.Crop(full, Regions.First));
            var mission = await reader.ReadTextAsync(GrayTemplateMatcher.Crop(full, Regions.Second));
            var counter = await counterReader.ReadAsync(GrayTemplateMatcher.Crop(full, Regions.Third));
            Console.WriteLine($"{frame.Timestamp:mm\\:ss\\.fff} counter={counter.Count} visibility={counter.Visibility} score={counter.SkullScore:F3}");
            var current = detector.Observe(new Helldivers2FrameObservation(frame.Timestamp, center, mission,
                counter.Text, counter.Visibility, counter.Count));
            foreach (var item in current)
            {
                detections.Add(item);
                Console.WriteLine($"{item.Timestamp:mm\\:ss\\.fff} {item.EventId} label={item.Label} confirmed={frame.Timestamp.TotalSeconds:F1}s confidence={item.Confidence:F2}");
            }
            if ((index + 1) % 25 == 0 || index + 1 == frames.Length)
                Console.Error.WriteLine($"Analyzed {index + 1}/{frames.Length} sampled frames");
        }
        return detections;
    }

    private static int Report(Fixture fixture, IReadOnlyList<Helldivers2DetectedEvent> detections)
    {
        var positives = fixture.Labels.Where(label => string.Equals(label.Kind, "positive", StringComparison.OrdinalIgnoreCase)
                                                      && !string.IsNullOrWhiteSpace(label.EventId)).ToArray();
        var matchedDetections = new HashSet<int>();
        var matchedLabels = 0;
        foreach (var label in positives)
        {
            var match = detections.Select((item, index) => (item, index))
                .Where(pair => !matchedDetections.Contains(pair.index)
                               && string.Equals(pair.item.EventId, label.EventId, StringComparison.OrdinalIgnoreCase)
                               && Math.Abs(pair.item.Timestamp.TotalSeconds - label.TimeSeconds) <= 5)
                .OrderBy(pair => Math.Abs(pair.item.Timestamp.TotalSeconds - label.TimeSeconds))
                .FirstOrDefault();
            if (match.item is null) continue;
            matchedDetections.Add(match.index);
            matchedLabels++;
        }

        var precision = detections.Count == 0 ? 0 : (double)matchedDetections.Count / detections.Count;
        var recall = positives.Length == 0 ? 1 : (double)matchedLabels / positives.Length;
        Console.WriteLine($"detections={detections.Count} positives={positives.Length} matched={matchedLabels} precision={precision:P1} recall={recall:P1}");
        return matchedLabels == positives.Length && matchedDetections.Count == detections.Count ? 0 : 1;
    }

    private sealed record Fixture(string RecordingId, IReadOnlyList<FixtureLabel> Labels);
    private sealed record FixtureLabel(double TimeSeconds, string Kind, string? EventId);
    private sealed record FixtureWindow(double StartSeconds, double DurationSeconds);
    private sealed record FrameSample(string Path, TimeSpan Timestamp);
}
