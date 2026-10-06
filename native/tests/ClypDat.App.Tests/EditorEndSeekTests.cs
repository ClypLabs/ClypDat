using System.Runtime.InteropServices;
using ClypDat.App.Services;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace ClypDat.App.Tests;

// A seek into a clip's final GOP lets libvlc's demuxer read ahead to end of
// file, which queues a decoder drain. Unpatched VLC 3.0's flush on the next
// seek did not cancel that drain; it ran once the post-seek blocks were
// decoded, threw away the codec's reference pictures, and every P-frame after
// resuming decoded grey or ghosted. native/vlc-core/patches carries the fix.
public sealed class EditorEndSeekTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ResumeAfterSeekingIntoFinalGopDecodesCleanly()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var directory = Path.Combine(Path.GetTempPath(), "ClypDat end seek " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // 120fps with a keyframe each second and a half-second final GOP,
            // like a ClypDat recording: nothing to recover from before the end.
            var clip = Path.Combine(directory, "clip.mp4");
            await SpotifyOverlayBurnerTests.Run("-f", "lavfi", "-i", "testsrc2=s=1280x720:r=120:d=2.5",
                "-c:v", "libx264", "-g", "120", "-bf", "0", "-pix_fmt", "yuv420p", clip);

            using var window = new HostWindow();
            using var playback = new PlaybackSession();
            playback.VideoPlayer.Hwnd = window.Handle;
            await playback.LoadVideoAsync(clip, "h264");
            // The first start after the test runtime changes also rebuilds
            // libvlc's plugin cache, which can overrun the editor's two-second
            // landing budget. Shipped builds carry a prebuilt cache; one retry
            // keeps this test about seeking, not about a cold cache.
            var start = await playback.StartCoordinatedAsync(TimeSpan.Zero);
            if (start.Outcome != EditorPlaybackStartOutcome.Playing)
                start = await playback.StartCoordinatedAsync(TimeSpan.Zero);
            Assert.Equal(EditorPlaybackStartOutcome.Playing, start.Outcome);
            await Task.Delay(300);
            var duration = playback.Duration;
            var back = duration - TimeSpan.FromMilliseconds(400);
            var nearEnd = duration - TimeSpan.FromMilliseconds(150);

            // Reference: the same resume, before anything has read to the end.
            playback.Pause();
            await playback.SeekAsync(back, resumePlayback: true);
            var reference = Snapshot(playback, directory, "reference");
            playback.Pause();

            for (var i = 0; i < 4; i++)
            {
                await playback.SeekAsync(nearEnd, resumePlayback: false);
                await Task.Delay(100);
                await playback.SeekAsync(back, resumePlayback: false);
                await Task.Delay(100);
                await playback.SeekAsync(back, resumePlayback: true);
                var shot = Snapshot(playback, directory, $"resume-{i}");
                playback.Pause();
                var difference = MeanDifference(reference, shot);
                output.WriteLine($"resume {i}: meanDifference={difference:F1}");
                Assert.True(difference < 12, $"Resume {i} decoded a corrupt picture (mean difference {difference:F1}).");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Snapshot(PlaybackSession playback, string directory, string name)
    {
        var path = Path.Combine(directory, name + ".png");
        playback.VideoPlayer.TakeSnapshot(0, path, 320, 180);
        return path;
    }

    private static double MeanDifference(string a, string b)
    {
        using var first = SKBitmap.Decode(a);
        using var second = SKBitmap.Decode(b);
        if (first is null || second is null) return 255;
        var width = Math.Min(first.Width, second.Width);
        var height = Math.Min(first.Height, second.Height);
        double total = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = first.GetPixel(x, y);
            var q = second.GetPixel(x, y);
            total += (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue)) / 3.0;
        }
        return total / (width * height);
    }

    // A real HWND for libvlc to render into, clipping its children like the
    // editor's video host, with its own message pump.
    private sealed class HostWindow : IDisposable
    {
        private const uint OverlappedWindow = 0x00CF0000, ClipChildren = 0x02000000, Quit = 0x0012;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private uint _threadId;
        public nint Handle { get; private set; }

        public HostWindow()
        {
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                Handle = CreateWindowExW(0, "STATIC", "ClypDat end seek test", OverlappedWindow | ClipChildren, -4000, -4000, 1280, 720, 0, 0, 0, 0);
                ShowWindow(Handle, 4);
                _ready.Set();
                while (GetMessageW(out var message, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
                DestroyWindow(Handle);
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait();
        }

        public void Dispose()
        {
            PostThreadMessageW(_threadId, Quit, 0, 0);
            _thread.Join(2000);
            _ready.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg { public nint Hwnd; public uint Message; public nint WParam; public nint LParam; public uint Time; public int X; public int Y; public uint Private; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] private static extern int GetMessageW(out Msg message, nint window, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg message);
        [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Msg message);
        [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }
}
