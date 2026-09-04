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
                    Assert.Equal(300, frame.Width);
                    Assert.Equal(66, frame.Height);
                    Assert.Equal(BrushColor(application, "AccentBrush"), Pixel(frame, 1, 20));
                    Assert.Equal(BrushColor(application, "SurfaceRaisedBrush"), Pixel(frame, 290, 20));
                    Assert.Equal(0u, Pixel(frame, 0, 0));
                    AssertPremultiplied(frame);
                }

                var original = ClipOverlayCardRenderer.Render(presentation);
                application.Resources["ClypDatFontFamily"] = new FontFamily("Courier New");
                var changedFont = ClipOverlayCardRenderer.Render(presentation);
                Assert.False(original.Pixels.AsSpan().SequenceEqual(changedFont.Pixels));

                var wrapped = ClipOverlayCardRenderer.Render(presentation with
                {
                    Event = presentation.Event with { Detail = new string('W', 80) }
                });
                Assert.True(wrapped.Height > original.Height);
                var scaled = ClipOverlayCardRenderer.Render(presentation with
                {
                    Event = presentation.Event with { Target = presentation.Event.Target with { Scaling = 1.5 } }
                });
                Assert.Equal(450, scaled.Width);
                Assert.Equal(99, scaled.Height);
                var failed = ClipOverlayCardRenderer.Render(presentation with
                {
                    Event = presentation.Event with { Kind = ClipOverlayKind.Failure }
                });
                Assert.Equal(BrushColor(application, "DangerBrush"), Pixel(failed, 1, 20));
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
