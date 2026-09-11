using System.Runtime.ExceptionServices;
using System.Text.Json;
using Avalonia;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CustomKeyboardRenderTests
{
    [Fact]
    [Trait("Category", "IsolatedSTA")]
    public void SavedCustomBoardRendersRecordedKeysAndMouseChoice()
    {
        AvaloniaTestThread.Run(() =>
        {
            var custom = new CustomKeyboardLayout { Keys = ["KeyW", "KeyA", "KeyS", "KeyD", "ShiftLeft", "Space"], IncludeMouse = false };
            var capture = new VideoOverlaySettings { KeyboardLayout = CustomKeyboardLibrary.Selection(custom) }.ToCaptureSettings([custom]);
            var layer = new ClipOverlayLayer(capture.KeyboardLayout, true, Keys: capture.KeyboardKeys!
                .Select(cap => new ClipOverlayKeyCap(cap.Code, cap.Label, cap.Row, cap.Units)).ToArray(), ShowMouse: capture.KeyboardShowMouse);
            var saved = JsonSerializer.Serialize(layer);
            custom.Keys.Clear(); custom.IncludeMouse = true;
            var restored = JsonSerializer.Deserialize<ClipOverlayLayer>(saved)!;
            var board = ClipOverlayManifest.BoardOf(restored)!;
            using var renderer = new KeyboardOverlayFrames(restored.Source, board, 480, 480);
            renderer.Render(new HashSet<string>()); renderer.CopyStraightPixels();
            var neutral = renderer.Pixels.ToArray();
            Assert.Contains(neutral, value => value != 0);
            renderer.Render(new HashSet<string> { "KeyW", "ShiftLeft" }); renderer.CopyStraightPixels();
            Assert.False(neutral.SequenceEqual(renderer.Pixels));
            renderer.Render(new HashSet<string> { "KeyP", "MouseLeft" }); renderer.CopyStraightPixels();
            Assert.Equal(neutral, renderer.Pixels);
            using var empty = new KeyboardOverlayFrames("QWERTY Full", CustomKeyboardBoard.Pack([], false), 240, 240);
            empty.Render(new HashSet<string>()); empty.CopyStraightPixels();
            Assert.All(empty.Pixels, value => Assert.Equal(0, value));
            using var mouse = new KeyboardOverlayFrames("QWERTY Full", CustomKeyboardBoard.Pack([], true), 240, 480);
            mouse.Render(new HashSet<string>()); mouse.CopyStraightPixels();
            var mouseNeutral = mouse.Pixels.ToArray();
            mouse.Render(new HashSet<string> { "KeyW" }); mouse.CopyStraightPixels();
            Assert.Equal(mouseNeutral, mouse.Pixels);
            mouse.Render(new HashSet<string> { "MouseLeft" }); mouse.CopyStraightPixels();
            Assert.False(mouseNeutral.SequenceEqual(mouse.Pixels));
        }, TimeSpan.FromSeconds(30), "Custom keyboard render timed out.");
    }
}
