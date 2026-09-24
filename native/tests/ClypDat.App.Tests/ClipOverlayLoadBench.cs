using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClypDat.App.Services;
using ClypDat.App.Views;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;
using Xunit.Abstractions;
using D3D11Api = Vortice.Direct3D11.D3D11;

namespace ClypDat.App.Tests;

// Manual measurement, not a regression gate: CLYPDAT_OVERLAY_BENCH=1 runs 100
// Saving -> Saved workflows through the real coordinator, native thread,
// DirectComposition and on-screen verification, above a topmost game window,
// idle and then under CPU saturation, GPU saturation, and both together with
// a disk-heavy remux. Replay recording is whatever the running app is doing.
public sealed class ClipOverlayLoadBench(ITestOutputHelper output)
{
    [Fact]
    public void PublishLatencyUnderLoad()
    {
        // "1" runs every scenario; otherwise a word picks those containing it.
        var selection = Environment.GetEnvironmentVariable("CLYPDAT_OVERLAY_BENCH");
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(selection)) return;
        foreach (var (name, load) in new (string, Func<IDisposable[]>)[]
        {
            ("idle", () => []),
            ("cpu saturated", () => [new CpuBurner()]),
            ("gpu saturated", () => [new GpuBurner()]),
            ("cpu+gpu+remux", () => [new CpuBurner(), new GpuBurner(), new Remuxer()])
        })
            if (selection == "1" || name.Contains(selection, StringComparison.OrdinalIgnoreCase)) Run(name, load);
    }

    private void Run(string scenario, Func<IDisposable[]> load)
    {
        var target = ClipOverlayTargeting.ResolvePrimary();
        using var game = new GameWindow(target.Bounds);
        target = target with { Window = game.Handle, Reason = ClipOverlayTargetReason.GameWindow };
        var counters = new ClipOverlayCounters();
        var reports = new ConcurrentBag<ClipOverlayPresentationReport>();
        var admissions = new ConcurrentBag<double>();
        var surface = new MeasuringSurface(new NativeClipOverlaySurface(_ => new ClipOverlayFrame(330, 87, new byte[330 * 87 * 4]), counters: counters), reports);
        var scheduler = new NativeClipOverlaySurfaceTests.ManualScheduler();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, counters: counters, log: (_, _) => { });
        var burners = load();
        var started = Stopwatch.StartNew();
        try
        {
            Thread.Sleep(1500); // Let the load reach steady state.
            var failures = 0;
            for (var index = 0; index < 100; index++)
            {
                var workflow = Guid.NewGuid();
                foreach (var (stage, kind) in new[] { (0, ClipOverlayKind.Saving), (1, ClipOverlayKind.Saved) })
                {
                    var presented = counters.Presented + counters.Unconfirmed;
                    surface.EventTicks = Stopwatch.GetTimestamp();
                    coordinator.Publish(NativeClipOverlaySurfaceTests.Event(workflow, stage, kind, DateTime.UtcNow, target));
                    admissions.Add(surface.AdmissionMs);
                    if (!SpinWait.SpinUntil(() => counters.Presented + counters.Unconfirmed > presented || counters.Failed > failures, 6000)) failures++;
                    failures = (int)Math.Max(failures, counters.Failed);
                }
                scheduler.FireDwell();
            }
            var sorted = admissions.OrderBy(value => value).ToArray();
            output.WriteLine($"== {scenario}: presented={counters.Presented} unconfirmed={counters.Unconfirmed} failed={counters.Failed} skipped={counters.Skipped} " +
                $"topmostRecoveries={counters.TopmostRecoveries} verificationRecoveries={counters.VerificationRecoveries}");
            output.WriteLine($"event->coordinator admitted: p50={sorted[sorted.Length / 2]:F3} p95={sorted[(int)(sorted.Length * .95)]:F3} max={sorted[^1]:F3}ms");
            output.WriteLine(NativeClipOverlaySurfaceTests.Latency(reports.ToArray()));
            foreach (var gpu in burners.OfType<GpuBurner>())
                output.WriteLine($"gpu load: {gpu.Draws / started.Elapsed.TotalSeconds:F0} full-screen 4K draws/s of a 1024-iteration trig shader");
            Assert.Equal(200, counters.Presented + counters.Unconfirmed);
        }
        finally
        {
            foreach (var burner in burners) burner.Dispose();
        }
    }

    private sealed class MeasuringSurface(NativeClipOverlaySurface inner, ConcurrentBag<ClipOverlayPresentationReport> reports) : IClipOverlaySurface
    {
        public long EventTicks;
        public double AdmissionMs;
        public void Publish(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion)
        {
            AdmissionMs = (Stopwatch.GetTimestamp() - EventTicks) * 1000.0 / Stopwatch.Frequency;
            inner.Publish(presentation, result => { if (result.Presented && result.Report is { } report) reports.Add(report); completion(result); });
        }
        public void Dismiss(long generation) => inner.Dismiss(generation);
        public void Dispose() => inner.Dispose();
    }

    // Every core spinning at normal priority, as a CPU-bound game would.
    private sealed class CpuBurner : IDisposable
    {
        private volatile bool _stop;
        private readonly Thread[] _threads;
        public CpuBurner()
        {
            _threads = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(() =>
            {
                var value = 1.0;
                while (!_stop) for (var i = 0; i < 100_000; i++) value = Math.Sqrt(value + i);
                GC.KeepAlive(value);
            }) { IsBackground = true }).ToArray();
            foreach (var thread in _threads) thread.Start();
        }
        public void Dispose() { _stop = true; foreach (var thread in _threads) thread.Join(); }
    }

    // A 4K render target filled by an expensive pixel shader as fast as the
    // GPU will take it, as a GPU-bound game would.
    private sealed class GpuBurner : IDisposable
    {
        private const string Shader =
            "struct V{float4 p:SV_Position;};V VS(uint id:SV_VertexID){V o;o.p=float4(id==2?3:-1,id==1?3:-1,0,1);return o;}" +
            "float4 PS(V i):SV_Target{float v=0;[loop]for(int k=0;k<1024;k++){v+=sin(i.p.x*0.001+k)*cos(i.p.y*0.001-k);}return float4(v,v,v,1);}";
        private volatile bool _stop;
        private readonly Thread _thread;
        public int Draws;
        public GpuBurner()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                D3D11Api.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, [FeatureLevel.Level_11_0], out var device, out _, out var context).CheckError();
                using (device) using (context)
                {
                    using var texture = device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, 3840, 2160, 1, 1, BindFlags.RenderTarget));
                    using var target = device.CreateRenderTargetView(texture);
                    using var vertex = device.CreateVertexShader(Compile("VS", "vs_5_0"));
                    using var pixel = device.CreatePixelShader(Compile("PS", "ps_5_0"));
                    ready.Set();
                    while (!_stop)
                    {
                        context.OMSetRenderTargets(target);
                        context.RSSetViewport(0, 0, 3840, 2160);
                        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                        context.VSSetShader(vertex);
                        context.PSSetShader(pixel);
                        context.Draw(3, 0);
                        if (++Draws % 4 == 0) context.Flush();
                    }
                }
            }) { IsBackground = true };
            _thread.Start();
            ready.Wait(5000);
        }
        public void Dispose() { _stop = true; _thread.Join(); }

        private static byte[] Compile(string entry, string profile)
        {
            var result = D3DCompile(Shader, Shader.Length, null, 0, 0, entry, profile, 0, 0, out var code, out var errors);
            if (errors != 0) Marshal.Release(errors);
            if (result != 0) throw new InvalidOperationException($"D3DCompile {entry} failed: 0x{result:X8}");
            try
            {
                var table = Marshal.ReadIntPtr(code);
                var pointer = Marshal.GetDelegateForFunctionPointer<BlobPointer>(Marshal.ReadIntPtr(table, 3 * IntPtr.Size))(code);
                var size = (int)Marshal.GetDelegateForFunctionPointer<BlobSize>(Marshal.ReadIntPtr(table, 4 * IntPtr.Size))(code);
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                return bytes;
            }
            finally { Marshal.Release(code); }
        }
        private delegate IntPtr BlobPointer(IntPtr blob);
        private delegate UIntPtr BlobSize(IntPtr blob);
        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        private static extern int D3DCompile(string source, int length, string? name, IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
    }

    // What a clip save does to the disk and a core: ffmpeg stream-copying a
    // real clip from the library, over and over.
    private sealed class Remuxer : IDisposable
    {
        private volatile bool _stop;
        private readonly Thread _thread;
        public Remuxer()
        {
            var ffmpeg = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "vendor", "ffmpeg", "ffmpeg.exe"));
            var clips = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClypDat", "Clips");
            var source = Directory.Exists(clips)
                ? new DirectoryInfo(clips).EnumerateFiles("*.mp4", SearchOption.AllDirectories).OrderByDescending(file => file.Length).FirstOrDefault()?.FullName
                : null;
            var output = Path.Combine(Path.GetTempPath(), $"clypdat-overlay-bench-{Environment.ProcessId}.mkv");
            _thread = new Thread(() =>
            {
                if (source is null || !File.Exists(ffmpeg)) return;
                while (!_stop)
                {
                    using var process = Process.Start(new ProcessStartInfo(ffmpeg, $"-hide_banner -loglevel error -y -i \"{source}\" -c copy \"{output}\"") { CreateNoWindow = true, UseShellExecute = false });
                    process?.WaitForExit();
                }
                try { File.Delete(output); } catch (IOException) { }
            }) { IsBackground = true };
            _thread.Start();
        }
        public void Dispose() { _stop = true; _thread.Join(60000); }
    }

    private sealed class GameWindow : IDisposable
    {
        private readonly Thread _thread;
        private uint _threadId;
        public GameWindow(Avalonia.PixelRect bounds)
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                // Zero-alpha layered and click-through: topmost for the z-order,
                // invisible on the display.
                Handle = CreateWindowEx(0x00000008 | 0x08000000 | 0x00000080 | 0x00080000 | 0x00000020, "STATIC", "ClypDat overlay bench game", 0x80000000, bounds.X, bounds.Y, 64, 64, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                SetLayeredWindowAttributes(Handle, 0, 0, 0x2);
                SetWindowPos(Handle, new IntPtr(-1), bounds.X, bounds.Y, 64, 64, 0x0010 | 0x0040);
                ready.Set();
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref message);
                DestroyWindow(Handle);
            }) { IsBackground = true };
            _thread.Start();
            ready.Wait(2000);
        }
        public IntPtr Handle { get; private set; }
        public void Dispose() { PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero); _thread.Join(2000); }
        [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public IntPtr Window; public uint Value; public IntPtr WParam, LParam; public uint Time; public int X, Y; public uint Private; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage message);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }
}
