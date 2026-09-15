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
            var reading = await reader.ReadAsync(Frame(index));
            output.WriteLine($"{index * 0.5:F1}: {reading.Visibility} {reading.SkullScore:F3} [{reading.Text}]");
            var current = detector.Observe(new Helldivers2FrameObservation(TimeSpan.FromSeconds(index * 0.5),
                "", "", reading.Text, reading.Visibility, reading.Count));
            if (current.Count > 0) confirmedAt = index * 0.5;
            events.AddRange(current);
        }
        var item = Assert.Single(events);
        Assert.Equal("killstreak-50", item.EventId);
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
            var reading = await reader.ReadAsync(Helldivers2CounterReader.Resize(Frame(index), width, height));
            output.WriteLine($"{index * 0.5:F1}: {reading.Visibility} {reading.SkullScore:F3} [{reading.Text}]");
            events.AddRange(detector.Observe(new Helldivers2FrameObservation(TimeSpan.FromSeconds(index * 0.5),
                "", "", reading.Text, reading.Visibility, reading.Count)));
        }
        var item = Assert.Single(events);
        Assert.Equal("Killstreak ×57", item.Label);
        Assert.Equal("killstreak-50", item.EventId);
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
