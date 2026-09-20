using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace ClypDat.App.Services;

/// <summary>
/// One still frame per display, for the capture-source chooser's preview tiles.
/// </summary>
// Windows Graphics Capture rather than Desktop Duplication on purpose: DXGI
// allows a single duplication per output per process, so grabbing a preview
// that way can collide with an armed replay buffer pointed at the same display.
// WGC has no such limit, and a chooser must never be able to disturb a
// recording that is already running.
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class MonitorThumbnailService
{
    // A tile is ~300px wide. Anything larger is thrown away by the downscale.
    private const int MaxWidth = 480;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromMilliseconds(600);

    public static async Task<Bitmap?> TryCaptureAsync(DesktopMonitorOption monitor, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return null;
        try
        {
            return await Task.Run(() => Capture(monitor, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        {
            // A missing preview is cosmetic - the tile falls back to its label.
            AppLog.Info($"Monitor preview unavailable for {monitor.Label}: {error.Message}");
            return null;
        }
    }

    private static Bitmap? Capture(DesktopMonitorOption monitor, CancellationToken cancellationToken)
    {
        var handle = MonitorFromPoint(new Point32(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2), MonitorDefaultToNearest);
        if (handle == IntPtr.Zero) return null;

        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out var created, out _, out ID3D11DeviceContext? createdContext);
        // The context this hands back is a separate wrapper from the one the
        // device caches; releasing it here is what keeps the grab from leaking
        // native memory (same reasoning as NativeReplayBuffer's device creation).
        createdContext?.Dispose();
        result.CheckError();
        using var device = created!;

        var item = CaptureInterop.CreateItemForMonitor(handle);
        if (item.Size.Width < 1 || item.Size.Height < 1) return null;

        var direct3DDevice = CaptureInterop.CreateDirect3DDevice(device);
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            direct3DDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var arrived = new ManualResetEventSlim(false);
        framePool.FrameArrived += (_, _) => arrived.Set();

        using var session = framePool.CreateCaptureSession(item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            // The chooser is not a recording, so it should not paint a capture
            // border around the user's screen while it is open.
            try { session.IsBorderRequired = false; } catch (Exception) { /* older build, live with the border */ }
        }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try { session.IsCursorCaptureEnabled = false; } catch (Exception) { /* cursor in the tile is harmless */ }
        }

        session.StartCapture();
        try
        {
            // The first pooled frame can be empty; wait for a real arrival, then
            // take whatever the pool is holding.
            if (!arrived.Wait(FrameTimeout, cancellationToken)) return null;
            using var frame = framePool.TryGetNextFrame();
            if (frame is null) return null;
            using var surface = CaptureInterop.GetTexture(frame.Surface);
            return Downscale(device, surface, cancellationToken);
        }
        finally
        {
            framePool.Dispose();
        }
    }

    private static unsafe Bitmap? Downscale(ID3D11Device device, ID3D11Texture2D source, CancellationToken cancellationToken)
    {
        var description = source.Description;
        var sourceWidth = (int)description.Width;
        var sourceHeight = (int)description.Height;
        if (sourceWidth < 1 || sourceHeight < 1) return null;

        using var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = description.Width,
            Height = description.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        });
        device.ImmediateContext.CopyResource(staging, source);
        var mapped = device.ImmediateContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scale = Math.Max(1, (sourceWidth + MaxWidth - 1) / MaxWidth);
            var targetWidth = Math.Max(1, sourceWidth / scale);
            var targetHeight = Math.Max(1, sourceHeight / scale);

            var bitmap = new WriteableBitmap(new PixelSize(targetWidth, targetHeight), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var buffer = bitmap.Lock();
            var sourceBase = (byte*)mapped.DataPointer;
            var sourcePitch = (int)mapped.RowPitch;
            for (var y = 0; y < targetHeight; y++)
            {
                var sourceRow = sourceBase + (long)Math.Min(sourceHeight - 1, y * scale) * sourcePitch;
                var targetRow = (byte*)buffer.Address + (long)y * buffer.RowBytes;
                for (var x = 0; x < targetWidth; x++)
                {
                    var sourcePixel = sourceRow + (long)Math.Min(sourceWidth - 1, x * scale) * 4;
                    var targetPixel = targetRow + (long)x * 4;
                    targetPixel[0] = sourcePixel[0];
                    targetPixel[1] = sourcePixel[1];
                    targetPixel[2] = sourcePixel[2];
                    targetPixel[3] = 255;
                }
            }

            return bitmap;
        }
        finally
        {
            device.ImmediateContext.Unmap(staging, 0);
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
        public Point32(int x, int y) { X = x; Y = y; }
    }

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);
}
