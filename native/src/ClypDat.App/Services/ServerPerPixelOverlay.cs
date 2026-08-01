using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace ClypDat.App.Services;

// Windows Server's redirected Avalonia surface turns transparent pixels into
// opaque black. Classic per-pixel layered windows work correctly there. This
// mirror draws an Avalonia control into that proven path while its almost
// invisible Avalonia owner remains responsible for layout and input.
internal sealed class ServerPerPixelOverlay : IDisposable
{
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;
    private const uint WsPopup = 0x80000000;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint UlwAlpha = 0x2;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private readonly Window _inputWindow;
    private readonly Control _source;
    private IntPtr _window;
    private IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _disposed;

    public ServerPerPixelOverlay(Window inputWindow, Control source)
    {
        _inputWindow = inputWindow;
        _source = source;
    }

    public void ShowAndRefresh()
    {
        if (_disposed || !WindowsPlatformProfile.IsServer()) return;
        if (!EnsureWindow()) return;
        Refresh();
        ShowWindow(_window, SwShowNoActivate);
    }

    public void Refresh()
    {
        if (_disposed || _window == IntPtr.Zero || !_inputWindow.IsVisible) return;
        if (!GetWindowRect(_inputWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, out var rect)) return;

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var scaling = _inputWindow.RenderScaling > 0 ? _inputWindow.RenderScaling : 1;

        try
        {
            using var bitmap = new RenderTargetBitmap(
                new PixelSize(width, height),
                new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(_source);

            EnsureSurface(width, height);
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), _bits, checked(width * height * 4), width * 4);

            var destination = new PointNative(rect.Left, rect.Top);
            var size = new SizeNative(width, height);
            var source = new PointNative(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1
            };

            if (!UpdateLayeredWindow(_window, IntPtr.Zero, ref destination, ref size, _memoryDc, ref source, 0, ref blend, UlwAlpha))
                AppLog.Error($"Per-pixel hover overlay update failed: error={Marshal.GetLastWin32Error()}.");

            var inputHandle = _inputWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (inputHandle != IntPtr.Zero)
                SetWindowPos(_window, inputHandle, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }
        catch (Exception error)
        {
            AppLog.Error("Per-pixel hover overlay render failed", error);
        }
    }

    public void Hide()
    {
        if (_window != IntPtr.Zero) ShowWindow(_window, SwHide);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        if (_window != IntPtr.Zero) DestroyWindow(_window);
        _window = IntPtr.Zero;
        DisposeSurface();
    }

    private bool EnsureWindow()
    {
        if (_window != IntPtr.Zero) return true;

        var owner = _inputWindow.Owner?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _window = CreateWindowEx(
            WsExLayered | WsExToolWindow | WsExNoActivate | WsExTransparent,
            "STATIC",
            string.Empty,
            WsPopup,
            0,
            0,
            1,
            1,
            owner,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_window != IntPtr.Zero) return true;

        AppLog.Error($"Per-pixel hover overlay create failed: error={Marshal.GetLastWin32Error()}.");
        return false;
    }

    private void EnsureSurface(int width, int height)
    {
        if (_memoryDc != IntPtr.Zero && _pixelWidth == width && _pixelHeight == height) return;
        DisposeSurface();

        _memoryDc = CreateCompatibleDC(IntPtr.Zero);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb
            }
        };
        _dib = CreateDIBSection(_memoryDc, ref info, DibRgbColors, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
            throw new InvalidOperationException($"CreateDIBSection failed: error={Marshal.GetLastWin32Error()}.");

        _oldBitmap = SelectObject(_memoryDc, _dib);
        _pixelWidth = width;
        _pixelHeight = height;
    }

    private void DisposeSurface()
    {
        if (_memoryDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) SelectObject(_memoryDc, _oldBitmap);
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
        _memoryDc = IntPtr.Zero;
        _dib = IntPtr.Zero;
        _oldBitmap = IntPtr.Zero;
        _bits = IntPtr.Zero;
        _pixelWidth = 0;
        _pixelHeight = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public PointNative(int x, int y) => (X, Y) = (x, y);
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeNative
    {
        public SizeNative(int width, int height) => (Width, Height) = (width, height);
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RectNative rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref PointNative destination, ref SizeNative size, IntPtr sourceDc, ref PointNative source, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr objectHandle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
