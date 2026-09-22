using ClypDat.App.Services;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace ClypDat.App.Tests;

public sealed class Helldivers2CounterReaderTests(ITestOutputHelper output)
{
    private static string FixtureRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
                directory = directory.Parent;
            return Path.Combine(directory!.FullName, "tests", "fixtures", "detector", "helldivers2");
        }
    }

    private static GrayDetectorImage Frame(int index) => GrayPng.Read(Path.Combine(FixtureRoot, $"counter-{index:D3}.png"));

    [Theory]
    [InlineData("110", "Killstreak ×110", 40.5)]
    [InlineData("58", "Killstreak ×58", 23)]
    [InlineData("77", null, 0)]
    [InlineData("45", null, 0)]
    public async Task BrightGameplayKeepsOneStreakUntilHudDisappears(string recording, string? expectedLabel, double disappearance)
    {
        var reader = new Helldivers2CounterReader(new WindowsOcrFrameReader().ReadTextAsync);
        var detector = new Helldivers2Detector();
        var events = new List<Helldivers2DetectedEvent>();
        var paths = Directory.GetFiles(Path.Combine(FixtureRoot, $"replay-{recording}"), "counter-*.png").Order().ToArray();
        Assert.NotEmpty(paths);
        var maximumRead = 0;
        var lastVisibility = Helldivers2CounterVisibility.Unknown;
        double confirmedAt = -1;
        for (var i = 0; i < paths.Length; i++)
        {
            var images = await GrayPng.ReadCounterAsync(paths[i]);
            var reading = await reader.ReadAsync(images.Gray, images.Mask);
            output.WriteLine($"{i * 0.5:F1}: {reading.Visibility} {reading.SkullScore:F3} [{reading.Text}]");
            maximumRead = Math.Max(maximumRead, reading.Count ?? 0);
            lastVisibility = reading.Visibility;
            var current = detector.Observe(new(TimeSpan.FromSeconds(i * 0.5), "", "", reading.Text, reading.Visibility, reading.Count));
            if (current.Count > 0) confirmedAt = i * 0.5;
            events.AddRange(current);
        }
        if (expectedLabel is null)
        {
            Assert.Empty(events);
            Assert.True(maximumRead >= (recording == "77" ? 100 : 55));
            Assert.NotEqual(Helldivers2CounterVisibility.Absent, lastVisibility);
        }
        else
        {
            var item = Assert.Single(events);
            Assert.Equal(expectedLabel, item.Label);
            Assert.InRange(item.Timestamp.TotalSeconds, disappearance - 0.5, disappearance + 0.5);
            Assert.Equal(item.Timestamp.TotalSeconds + 1, confirmedAt);
        }
    }

    [Fact]
    public async Task RecordingCompletesOnceAt57AfterDisappearance()
    {
        var ocr = new WindowsOcrFrameReader();
        var reader = new Helldivers2CounterReader(ocr.ReadTextAsync);
        var detector = new Helldivers2Detector();
        var events = new List<Helldivers2DetectedEvent>();
        double confirmedAt = -1;
        for (var index = 0; index < 60; index++)
        {
            var images = await GrayPng.ReadCounterAsync(Path.Combine(FixtureRoot, $"counter-{index:D3}.png"));
            var reading = await reader.ReadAsync(images.Gray, images.Mask);
            output.WriteLine($"{index * 0.5:F1}: {reading.Visibility} {reading.SkullScore:F3} [{reading.Text}]");
            var current = detector.Observe(new Helldivers2FrameObservation(TimeSpan.FromSeconds(index * 0.5),
                "", "", reading.Text, reading.Visibility, reading.Count));
            if (current.Count > 0) confirmedAt = index * 0.5;
            events.AddRange(current);
        }
        var item = Assert.Single(events);
        Assert.Equal("killstreak", item.EventId);
        Assert.Equal("Killstreak ×57", item.Label);
        Assert.InRange(item.Timestamp.TotalSeconds, 28, 28.5);
        Assert.Equal(item.Timestamp.TotalSeconds + 1, confirmedAt);
        Assert.InRange(item.StreakStart!.Value.TotalSeconds, 0, 1.5);
    }

    [Theory]
    [InlineData(0, Helldivers2CounterVisibility.Present)]
    [InlineData(2, Helldivers2CounterVisibility.Present)]
    [InlineData(20, Helldivers2CounterVisibility.Present)]
    [InlineData(50, Helldivers2CounterVisibility.Present)]
    [InlineData(55, Helldivers2CounterVisibility.Present)]
    [InlineData(56, Helldivers2CounterVisibility.Absent)]
    [InlineData(59, Helldivers2CounterVisibility.Absent)]
    public async Task SkullSurvivesGrowthAndFade(int index, Helldivers2CounterVisibility expected)
    {
        var reader = new Helldivers2CounterReader(_ => Task.FromResult(""));
        var reading = await reader.ReadAsync(Frame(index));
        output.WriteLine($"score={reading.SkullScore:F3}");
        Assert.Equal(expected, reading.Visibility);
    }

    [Theory]
    [InlineData(214, 121)] // 1000p minimum supported height.
    [InlineData(231, 130)] // 1080p slot, rounded by DetectorRegions.
    [InlineData(308, 174)] // 1440p slot.
    [InlineData(462, 261)] // 2160p slot.
    public async Task SupportedSlotSizesKeepFinalCountAndVisibility(int width, int height)
    {
        var ocr = new WindowsOcrFrameReader();
        var reader = new Helldivers2CounterReader(ocr.ReadTextAsync);
        var detector = new Helldivers2Detector();
        var events = new List<Helldivers2DetectedEvent>();
        for (var index = 0; index < 60; index++)
        {
            var images = await GrayPng.ReadCounterAsync(Path.Combine(FixtureRoot, $"counter-{index:D3}.png"));
            var reading = await reader.ReadAsync(Helldivers2CounterReader.Resize(images.Gray, width, height),
                Helldivers2CounterReader.Resize(images.Mask, width, height));
            output.WriteLine($"{index * 0.5:F1}: {reading.Visibility} {reading.SkullScore:F3} [{reading.Text}]");
            events.AddRange(detector.Observe(new Helldivers2FrameObservation(TimeSpan.FromSeconds(index * 0.5),
                "", "", reading.Text, reading.Visibility, reading.Count)));
        }
        var item = Assert.Single(events);
        Assert.Equal("Killstreak ×57", item.Label);
        Assert.Equal("killstreak", item.EventId);
        Assert.InRange(item.Timestamp.TotalSeconds, 28, 28.5);
    }

    [Fact]
    public async Task InvalidCaptureIsUnknownAndFlatBackgroundIsAbsent()
    {
        var reader = new Helldivers2CounterReader(_ => throw new InvalidOperationException("Background must not run OCR."));
        Assert.Equal(Helldivers2CounterVisibility.Unknown, (await reader.ReadAsync(new(0, 0, []))).Visibility);
        foreach (var level in new byte[] { 0, 80, 255 })
            Assert.Equal(Helldivers2CounterVisibility.Absent,
                (await reader.ReadAsync(new(308, 174, Enumerable.Repeat(level, 308 * 174).ToArray()))).Visibility);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task OcrTriesMaskThenGrayscaleAndStopsAtFirstCount(int successAt)
    {
        var images = await GrayPng.ReadCounterAsync(Path.Combine(FixtureRoot, "counter-050.png"));
        var seen = new List<GrayDetectorImage>();
        var reader = new Helldivers2CounterReader(image =>
        {
            seen.Add(image);
            return Task.FromResult(seen.Count == successAt ? successAt % 2 == 1 ? "x110" : "110" : "");
        });
        var reading = await reader.ReadAsync(images.Gray, images.Mask);
        Assert.Equal(110, reading.Count);
        Assert.Equal(successAt, seen.Count);
        for (var i = 0; i < seen.Count; i++)
        {
            var hud = Helldivers2CounterReader.Resize(i < 2 ? images.Mask : images.Gray, 308, 174);
            Assert.Equal(Helldivers2CounterReader.PrepareNumber(hud, i % 2 == 1).Pixels, seen[i].Pixels);
        }
    }

    [Fact]
    public async Task AbsentMaskCannotBeOverriddenByGrayscaleHud()
    {
        var image = Frame(50);
        var reader = new Helldivers2CounterReader(_ => throw new InvalidOperationException("Absent HUD must not run OCR."));
        Assert.Equal(Helldivers2CounterVisibility.Absent,
            (await reader.ReadAsync(image, image with { Pixels = new byte[image.Pixels.Length] })).Visibility);
        Assert.Equal(Helldivers2CounterVisibility.Unknown,
            (await reader.ReadAsync(image, new(1, 1, [255]))).Visibility);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(123)]
    [InlineData(568)]
    public async Task ThreeDigitCountersFitTheNumberCrop(int count)
    {
        using var bitmap = new SKBitmap(112, 48);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        using var font = new SKFont(typeface, 28);
        using var paint = new SKPaint { Color = SKColors.Yellow, IsAntialias = true };
        canvas.DrawText($"x{count}", 0, 34, SKTextAlign.Left, font, paint);
        var hud = Frame(50);
        var pixels = (byte[])hud.Pixels.Clone();
        for (var y = 0; y < 48; y++)
        for (var x = 0; x < 112; x++)
        {
            var color = bitmap.GetPixel(x, y);
            pixels[(56 + y) * hud.Width + 120 + x] = (byte)((color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000);
        }
        var ocr = new WindowsOcrFrameReader();
        var reading = await new Helldivers2CounterReader(ocr.ReadTextAsync).ReadAsync(hud with { Pixels = pixels });
        output.WriteLine(reading.Text);
        Assert.Equal(count, reading.Count);
    }
}
