using ClypDat.App.Controls;
using SkiaSharp;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AnimatedAvatarTests
{
    // A 4x4 GIF with two frames, red for 120 ms then blue for 250 ms.
    private const string TwoFrameGif =
        "R0lGODlhBAAEAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQADAAAACwAAAAABAAEAAAICQABCBxIsCCAgAAh+QQBGQABACwAAAAABAAEAIEAAP8AAAAAAAAAAAAICQABCBxIsCCAgAA7";

    [Fact]
    [Trait("Category", "IsolatedSTA")]
    public void AnimatedGifKeepsEveryFrameAndItsDuration()
    {
        if (!OperatingSystem.IsWindows()) return;
        AvaloniaTestThread.Run(() =>
        {
            using var avatar = AnimatedAvatar.Decode(Convert.FromBase64String(TwoFrameGif), 80);
            Assert.True(avatar.IsAnimated);
            Assert.Equal(2, avatar.Frames.Count);
            Assert.Equal(TimeSpan.FromMilliseconds(120), avatar.Durations[0]);
            Assert.Equal(TimeSpan.FromMilliseconds(250), avatar.Durations[1]);
        }, TimeSpan.FromSeconds(30), "Decoding the test GIF timed out.");
    }

    [Fact]
    [Trait("Category", "IsolatedSTA")]
    public void StillPictureIsOneFrameScaledToTheRequestedWidth()
    {
        if (!OperatingSystem.IsWindows()) return;
        AvaloniaTestThread.Run(() =>
        {
            using var bitmap = new SKBitmap(128, 128);
            bitmap.Erase(SKColors.Green);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);

            using var avatar = AnimatedAvatar.Decode(png.ToArray(), 80);
            Assert.False(avatar.IsAnimated);
            Assert.Single(avatar.Frames);
            Assert.Equal(80, avatar.Frames[0].PixelSize.Width);
        }, TimeSpan.FromSeconds(30), "Decoding the test PNG timed out.");
    }
}
