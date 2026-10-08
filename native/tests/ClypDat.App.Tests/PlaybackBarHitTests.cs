using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ClypDat.App.Controls;
using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class PlaybackBarHitTests
{
    [Fact]
    public void HoverSeekStripReceivesInputAcrossItsFullHeightAndWidth()
    {
        AvaloniaTestThread.Run(() =>
        {
            var owner = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            var backdrop = (Border)typeof(MainWindow).GetMethod("BuildPlaybackControlsBackdrop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(owner, [false, new TranslateTransform()])!;
            var window = new Window
            {
                Width = 900, Height = 62, ShowActivated = false, ShowInTaskbar = false,
                WindowDecorations = WindowDecorations.None, Position = new PixelPoint(-16000, -16000),
                CanResize = false, Background = Brushes.Transparent,
                TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                Content = new Border { Background = Brushes.Transparent, ClipToBounds = true, Child = backdrop }
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var renderer = typeof(TopLevel).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                renderer.GetType().GetMethod("Paint", [typeof(Rect)])!.Invoke(renderer, [new Rect(window.ClientSize)]);
                var rail = window.GetVisualDescendants().OfType<SeekRailControl>().Single();
                var strip = (Border)rail.Parent!;
                Assert.Equal(16, strip.Bounds.Height);
                var stripOrigin = strip.TranslatePoint(default, backdrop)!.Value;
                Assert.InRange(stripOrigin.Y, 0, backdrop.Bounds.Height - strip.Bounds.Height);
                using var bitmap = new RenderTargetBitmap(new PixelSize(900, 62), new Vector(96, 96));
                bitmap.Render((Control)window.Content!);
                var pixels = new byte[900 * 62 * 4];
                var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try { bitmap.CopyPixels(new PixelRect(0, 0, 900, 62), pinned.AddrOfPinnedObject(), pixels.Length, 900 * 4); }
                finally { pinned.Free(); }
                Assert.Equal(0, pixels[(7 * 900 + 450) * 4 + 3]);
                Assert.Equal(1, pixels[(13 * 900 + 450) * 4 + 3]);
                Assert.InRange(pixels[(20 * 900 + 450) * 4 + 3], 140, 141);
                foreach (var x in new[] { 1d, 8d, 450d, 892d, 899d })
                foreach (var y in new[] { 1d, 5d, 8d, 15d })
                {
                    var point = strip.TranslatePoint(new Point(x, y), window)!.Value;
                    Assert.Same(strip, window.InputHitTest(point));
                    Assert.True(pixels[((int)point.Y * 900 + (int)point.X) * 4 + 3] > 0,
                        $"The seek strip must retain a native input surface at {point}.");
                }
            }
            finally { window.Close(); }
        }, TimeSpan.FromSeconds(30), "Hover seek hit test did not finish.");
    }

    [Fact]
    public void HoverRailPaintsBothVideoEdges()
    {
        AvaloniaTestThread.Run(() =>
        {
            var owner = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            var layout = (Control)typeof(MainWindow).GetMethod("BuildPlaybackBarLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(owner, [false])!;
            var rail = layout.GetLogicalDescendants().OfType<SeekRailControl>().Single();
            rail.Duration = TimeSpan.FromSeconds(10);
            rail.Position = TimeSpan.FromSeconds(5);
            rail.TrackBrush = Brushes.Blue;
            rail.PlayedBrush = Brushes.Red;
            rail.Measure(new Size(100, 16));
            rail.Arrange(new Rect(0, 0, 100, 16));
            using var bitmap = new RenderTargetBitmap(new PixelSize(100, 16), new Vector(96, 96));
            bitmap.Render(rail);
            var pixels = new byte[100 * 16 * 4];
            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { bitmap.CopyPixels(new PixelRect(0, 0, 100, 16), pinned.AddrOfPinnedObject(), pixels.Length, 100 * 4); }
            finally { pinned.Free(); }
            Assert.Equal(255, pixels[(8 * 100) * 4 + 2]);
            Assert.Equal(255, pixels[(8 * 100 + 99) * 4]);
        }, TimeSpan.FromSeconds(30), "Hover rail edge rendering did not finish.");
    }
}
