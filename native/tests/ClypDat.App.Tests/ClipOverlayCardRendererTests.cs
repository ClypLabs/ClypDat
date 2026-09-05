using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using ClypDat.App.Services;
using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipOverlayCardRendererTests
{
    [Fact]
    public void RasterUsesCurrentThemeFontDpiAndMeasuredWrapping()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<Application>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
                var application = Application.Current!;
                application.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://ClypDat/"))
                    { Source = new Uri("avares://ClypDat/Styles/Tokens.axaml") });
                application.Resources["AccentBrush"] = new SolidColorBrush(Colors.Blue);
                application.Resources["ClypDatFontFamily"] = new FontFamily("fonts:Inter#Inter, $Default");
                var now = DateTime.UtcNow;
                var presentation = new ClipOverlayPresentation(1, new ClipOverlayEvent(Guid.NewGuid(), 0, now, now,
                    80, ClipOverlayKind.Saved, "Clip Saved", null,
                    new ClipOverlayTarget("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040),
                        1, ClipOverlayTargetReason.Primary), ClipOverlayPlacement.TopRight, true));

                foreach (var theme in new[] { "Emerald", "Berry", "Light" })
                {
                    AppThemeService.Apply(application, theme, Colors.Blue, false);
                    var frame = ClipOverlayCardRenderer.Render(presentation);
                    Assert.Equal(220, frame.Width);
                    Assert.Equal(58, frame.Height);
                    Assert.Equal(BrushColor(application, "AccentBrush"), Pixel(frame, frame.Width - 1, 20));
                    AssertFill(BrushColor(application, "SurfaceBrush"), Pixel(frame, frame.Width - 20, 20));
                    Assert.Equal(0u, Pixel(frame, 0, 0));
                    AssertPremultiplied(frame);
                    var leftFrame = ClipOverlayCardRenderer.Render(presentation with
                    {
                        Event = presentation.Event with { Placement = ClipOverlayPlacement.TopLeft }
                    });
                    Assert.Equal(BrushColor(application, "AccentBrush"), Pixel(leftFrame, 0, 0));
                    Assert.Equal(0u, Pixel(leftFrame, leftFrame.Width - 1, 0));
                }

                var original = ClipOverlayCardRenderer.Render(presentation);
                application.Resources["ClypDatFontFamily"] = new FontFamily("Courier New");
                var changedFont = ClipOverlayCardRenderer.Render(presentation);
                Assert.False(original.Pixels.AsSpan().SequenceEqual(changedFont.Pixels));
                application.Resources["ClypDatFontFamily"] = new FontFamily("fonts:Inter#Inter, $Default");

                var wrapped = ClipOverlayCardRenderer.Render(presentation with
                {
                    Event = presentation.Event with { Detail = new string('W', 80) }
                });
                Assert.True(wrapped.Height > original.Height);
                var scaled = ClipOverlayCardRenderer.Render(presentation with
                {
                    Event = presentation.Event with { Target = presentation.Event.Target with { Scaling = 1.5 } }
                });
                Assert.Equal(330, scaled.Width);
                Assert.Equal(87, scaled.Height);
                var failed = ClipOverlayCardRenderer.Render(presentation with
                {
                    Event = presentation.Event with { Kind = ClipOverlayKind.Failure }
                });
                Assert.Equal(BrushColor(application, "DangerBrush"), Pixel(failed, failed.Width - 1, 20));

                AppThemeService.Apply(application, "Emerald", Colors.Blue, false);
                var recording = presentation.Event with
                {
                    Kind = ClipOverlayKind.GameStarted, Title = "Recording: Doom", Detail = null
                };
                var bare = ClipOverlayCardRenderer.Render(new ClipOverlayPresentation(2, recording));
                var chipped = ClipOverlayCardRenderer.Render(new ClipOverlayPresentation(3,
                    recording with { Hotkey = "Ctrl+Shift+F9", HotkeyHint = "to save a clip" }));
                Assert.True(chipped.Height > bare.Height, "The keycap row has to add a second line.");
                Assert.False(bare.Pixels.AsSpan().SequenceEqual(chipped.Pixels));

                // The regression this design restores: a long game name widens
                // the card instead of wrapping onto a second title line.
                var shortTitle = ClipOverlayCardRenderer.Render(new ClipOverlayPresentation(4,
                    recording with { Hotkey = "Insert", HotkeyHint = "to save a clip" }));
                var longTitle = ClipOverlayCardRenderer.Render(new ClipOverlayPresentation(5,
                    recording with { Title = "Recording: HELLDIVERS™ 2", Hotkey = "Insert", HotkeyHint = "to save a clip" }));
                Assert.True(longTitle.Width > shortTitle.Width, "The card has to size itself to the title.");
                Assert.Equal(shortTitle.Height, longTitle.Height);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Offscreen rasterization timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static uint BrushColor(Application application, string key)
    {
        Assert.True(application.Resources.TryGetResource(key, application.ActualThemeVariant, out var value));
        return Assert.IsType<SolidColorBrush>(value).Color.ToUInt32();
    }

    // The card fill is drawn at 96% so gameplay bleeds through, and the raster
    // is premultiplied - so the expected pixel is the token scaled by that
    // alpha, within whatever rounding the renderer applies on the way.
    private static void AssertFill(uint token, uint pixel)
    {
        const double alpha = 0.96;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var expected = ((token >> shift) & 0xFF) * alpha;
            var actual = (pixel >> shift) & 0xFF;
            Assert.True(Math.Abs(expected - actual) <= 2,
                $"Fill channel at bit {shift}: expected ~{expected:F0}, got {actual} (token 0x{token:X8}, pixel 0x{pixel:X8}).");
        }
    }

    private static uint Pixel(ClipOverlayFrame frame, int x, int y)
        => BitConverter.ToUInt32(frame.Pixels, (y * frame.Width + x) * 4);

    private static void AssertPremultiplied(ClipOverlayFrame frame)
    {
        for (var i = 0; i < frame.Pixels.Length; i += 4)
        {
            var alpha = frame.Pixels[i + 3];
            Assert.True(frame.Pixels[i] <= alpha && frame.Pixels[i + 1] <= alpha && frame.Pixels[i + 2] <= alpha);
        }
    }
}
