using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

// One native notification surface. Its HWND, DIB and timer all live on a
// dedicated thread; Avalonia's frame clock never participates.
internal sealed unsafe class NativeClipOverlaySurface : IClipOverlaySurface
{
    private const int TimerId = 1;
    private const uint WmAppPublish = 0x8001, WmAppDismiss = 0x8002, WmClose = 0x0010, WmDestroy = 0x0002, WmTimer = 0x0113;
    private const int WsExLayered = 0x00080000, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private const uint WsPopup = 0x80000000, UlwAlpha = 2, WdaExcludeFromCapture = 0x11;
    private const uint DtWordBreak = 0x10, DtNoPrefix = 0x800;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly ConcurrentDictionary<nint, NativeClipOverlaySurface> Instances = new();
    private static readonly WindowProc SharedWindowProc = WindowProcedure;
    private static readonly object InterFontGate = new();
    private static int _classRegistered;
    private static readonly List<byte[]> InterFontData = [];
    private static readonly List<nint> InterFontHandles = [];

    // Captured on the UI thread at startup (App.PreloadOverlayFonts) rather
    // than read here. AssetLoader resolves IAssetLoader out of AvaloniaLocator,
    // and from this surface's own thread that lookup fails outright - "Unable
    // to locate 'Avalonia.Platform.IAssetLoader'" - so GDI never got the
    // bundled Inter and every notification rendered in the fallback face
    // instead of the app's.
    internal static byte[][]? PreloadedFontData;

    // Follows Settings > Fonts. This was hard-coded to "Inter", which left the
    // notification the one part of the app still in the old face whenever the
    // user picked anything else.
    private static volatile string _fontFace = "Inter";
    internal static string FontFace
    {
        get => _fontFace;
        set => _fontFace = string.IsNullOrWhiteSpace(value) ? "Inter" : value.Trim();
    }

    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private ClipOverlayPresentation? _pending, _current;
    private long _pendingDismissal, _motionStarted;
    private nint _window, _memoryDc, _bitmap, _oldBitmap, _bits;
    private int _width, _height, _publishCount;
    private Motion _motion;
    private bool _visible, _disposed;

    public NativeClipOverlaySurface()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ClypDat clip overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    internal nint WindowHandle => _window;
    internal int PublishCount => Volatile.Read(ref _publishCount);

    public void Publish(ClipOverlayPresentation presentation)
    {
        lock (_gate) { if (_disposed) return; _pending = presentation; }
        if (_window != 0) PostMessage(_window, WmAppPublish, 0, 0);
    }

    public void Dismiss(long generation)
    {
        lock (_gate) { if (_disposed) return; _pendingDismissal = generation; }
        if (_window != 0) PostMessage(_window, WmAppDismiss, 0, 0);
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        if (_window != 0) PostMessage(_window, WmClose, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            EnsureOverlayFont();
            RegisterWindowClass();
            _window = CreateWindowEx(WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate,
                ClassName, string.Empty, WsPopup, -32000, -32000, 1, 1, 0, 0, GetModuleHandle(null), 0);
            if (_window == 0) throw new InvalidOperationException($"Could not create clip overlay window ({Marshal.GetLastWin32Error()}).");
            Instances[_window] = this;
            SetTimer(_window, TimerId, 15, 0);
            _ready.Set();
            while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception error) { AppLog.Error("Native clip overlay thread failed", error); _ready.Set(); }
        finally { DestroyBitmap(); if (_window != 0) Instances.TryRemove(_window, out _); _window = 0; }
    }

    private void AcceptPublish()
    {
        ClipOverlayPresentation? presentation;
        lock (_gate) { presentation = _pending; _pending = null; }
        if (presentation is null) return;
        _current = presentation;
        Render(presentation);
        SetWindowDisplayAffinity(_window, presentation.Event.ExcludeFromCapture ? WdaExcludeFromCapture : 0);
        _motion = Motion.Entering;
        _motionStarted = Stopwatch.GetTimestamp();
        _visible = true;
        ShowWindow(_window, 4);
        SetWindowPos(_window, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        Present(0);
        Interlocked.Increment(ref _publishCount);
    }

    private void AcceptDismissal()
    {
        long generation;
        lock (_gate) generation = _pendingDismissal;
        if (!_visible || _current?.Generation != generation) return;
        _motion = Motion.Exiting;
        _motionStarted = Stopwatch.GetTimestamp();
    }

    private void Tick()
    {
        if (!_visible || _current is null) return;
        var elapsed = Stopwatch.GetElapsedTime(_motionStarted).TotalMilliseconds;
        if (_motion == Motion.Entering)
        {
            Present(EaseOut(Math.Min(1, elapsed / 220)));
            if (elapsed >= 220) { _motion = Motion.Still; Present(1); }
        }
        else if (_motion == Motion.Exiting)
        {
            Present(1 - EaseIn(Math.Min(1, elapsed / 180)));
            if (elapsed >= 180 || elapsed >= 400) Hide();
        }
    }

    private void Hide() { ShowWindow(_window, 0); _visible = false; _motion = Motion.Still; _current = null; }

    private void Present(double progress)
    {
        if (_current is null || _memoryDc == 0) return;
        var target = _current.Event.Target;
        var placement = _current.Event.Placement;
        var final = ClipOverlayLayout.Position(target, placement, _width, _height);
        var left = placement is ClipOverlayPlacement.TopLeft or ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.BottomLeft;
        var offset = (int)Math.Round(24 * target.Scaling * (1 - progress)) * (left ? -1 : 1);
        var destination = new PointNative(final.X + offset, final.Y);
        var source = new PointNative(0, 0);
        var size = new SizeNative(_width, _height);
        var blend = new BlendFunction(0, 0, (byte)Math.Clamp((int)Math.Round(255 * progress), 0, 255), 1);
        UpdateLayeredWindow(_window, 0, ref destination, ref size, _memoryDc, ref source, 0, ref blend, UlwAlpha);
    }

    private void Render(ClipOverlayPresentation presentation)
    {
        var scale = Math.Clamp(presentation.Event.Target.Scaling, 0.75, 4);
        var width = (int)Math.Round(300 * scale);
        var lines = string.IsNullOrWhiteSpace(presentation.Event.Detail) ? 0 : Math.Max(1, (int)Math.Ceiling(presentation.Event.Detail!.Length / 38.0));
        var height = (int)Math.Round((66 + Math.Max(0, lines - 1) * 20) * scale);
        CreateBitmap(width, height);
        var pixels = (uint*)_bits;
        var radius = (int)Math.Round(8 * scale);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (InsideRoundedRect(x, y, width, height, radius)) pixels[y * width + x] = 0xFF161A1F;

        var rail = Math.Max(3, (int)Math.Round(4 * scale));
        for (var y = radius; y < height - radius; y++)
        for (var x = 0; x < rail; x++) pixels[y * width + x] = presentation.AccentColor;

        var iconX = (int)Math.Round(16 * scale);
        var iconSize = (int)Math.Round(28 * scale);
        var iconY = (height - iconSize) / 2;

        SetBkMode(_memoryDc, 1);
        var face = FontFace;
        var titleFont = CreateFont(-(int)Math.Round(18 * scale), 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 5, 0, face);
        var detailFont = CreateFont(-(int)Math.Round(16 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, face);
        try
        {
            DrawApplicationIcon(iconX, iconY, iconSize);
            var old = SelectObject(_memoryDc, titleFont);
            SetTextColor(_memoryDc, ColorRef(242, 244, 249));
            var textX = (int)Math.Round(58 * scale);
            var titleTop = string.IsNullOrWhiteSpace(presentation.Event.Detail) ? (int)Math.Round(22 * scale) : (int)Math.Round(10 * scale);
            var titleRect = new RectNative(textX, titleTop, width - (int)Math.Round(18 * scale), height);
            DrawText(_memoryDc, presentation.Event.Title, -1, ref titleRect, DtNoPrefix);
            if (!string.IsNullOrWhiteSpace(presentation.Event.Detail))
            {
                SelectObject(_memoryDc, detailFont);
                SetTextColor(_memoryDc, ColorRef(189, 198, 225));
                var detailRect = new RectNative(textX, (int)Math.Round(34 * scale), width - (int)Math.Round(16 * scale), height - (int)Math.Round(8 * scale));
                DrawText(_memoryDc, presentation.Event.Detail!, -1, ref detailRect, DtWordBreak | DtNoPrefix);
            }
            SelectObject(_memoryDc, old);
        }
        finally { DeleteObject(titleFont); DeleteObject(detailFont); }
    }

    private void DrawApplicationIcon(int x, int y, int size)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        if (ExtractIconEx(executable, 0, out var large, out var small, 1) == 0) return;
        try
        {
            var icon = large != 0 ? large : small;
            if (icon != 0) DrawIconEx(_memoryDc, x, y, icon, size, size, 0, 0, 3);
        }
        finally
        {
            if (large != 0) DestroyIcon(large);
            if (small != 0) DestroyIcon(small);
        }
    }

    private void CreateBitmap(int width, int height)
    {
        DestroyBitmap();
        _width = width; _height = height; _memoryDc = CreateCompatibleDC(0);
        var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 } };
        _bitmap = CreateDIBSection(_memoryDc, ref info, 0, out _bits, 0, 0);
        _oldBitmap = SelectObject(_memoryDc, _bitmap);
        new Span<byte>((void*)_bits, width * height * 4).Clear();
    }

    private void DestroyBitmap()
    {
        if (_memoryDc != 0 && _oldBitmap != 0) SelectObject(_memoryDc, _oldBitmap);
        if (_bitmap != 0) DeleteObject(_bitmap);
        if (_memoryDc != 0) DeleteDC(_memoryDc);
        _memoryDc = _bitmap = _oldBitmap = _bits = 0;
    }

    private static bool InsideRoundedRect(int x, int y, int width, int height, int radius)
    {
        var cx = x < radius ? radius : x >= width - radius ? width - radius - 1 : x;
        var cy = y < radius ? radius : y >= height - radius ? height - radius - 1 : y;
        var dx = x - cx; var dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static void FillRounded(uint* pixels, int stride, int canvasHeight, int left, int top, int width, int height, int radius, uint color)
    {
        for (var y = Math.Max(0, top); y < Math.Min(canvasHeight, top + height); y++)
        for (var x = Math.Max(0, left); x < Math.Min(stride, left + width); x++)
            if (InsideRoundedRect(x - left, y - top, width, height, radius)) pixels[y * stride + x] = color;
    }

    private static double EaseOut(double value) => 1 - Math.Pow(1 - value, 3);
    private static double EaseIn(double value) => value * value * value;
    private static uint ColorRef(byte red, byte green, byte blue) => (uint)(red | green << 8 | blue << 16);
    private static string ClassName => "ClypDat.NativeClipOverlay";

    private static void RegisterWindowClass()
    {
        if (Interlocked.Exchange(ref _classRegistered, 1) != 0) return;
        var value = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null), ClassName = ClassName, WindowProcedure = SharedWindowProc };
        if (RegisterClassEx(ref value) == 0 && Marshal.GetLastWin32Error() != 1410)
            throw new InvalidOperationException($"Could not register clip overlay class ({Marshal.GetLastWin32Error()}).");
    }

    // Avalonia bundles Inter inside Avalonia.Fonts.Inter; Windows does not
    // register that private font for GDI, so it goes into this process's own
    // font table. Only Inter needs this - any font picked from Settings >
    // Fonts is installed on the machine and GDI already knows its face name.
    private static void EnsureOverlayFont()
    {
        lock (InterFontGate)
        {
            if (InterFontHandles.Count != 0) return;
            var assets = PreloadedFontData;
            if (assets is null || assets.Length == 0)
            {
                AppLog.Info("Native clip overlay has no preloaded font data; notifications fall back to the GDI default face.");
                return;
            }

            foreach (var fontData in assets)
            {
                fixed (byte* pointer = fontData)
                {
                    var handle = AddFontMemResourceEx((nint)pointer, (uint)fontData.Length, 0, out _);
                    if (handle != 0) { InterFontData.Add(fontData); InterFontHandles.Add(handle); }
                }
            }

            if (InterFontHandles.Count == 0) AppLog.Info("Native clip overlay could not register the bundled Inter font with GDI.");
        }
    }

    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (!Instances.TryGetValue(window, out var instance)) return DefWindowProc(window, message, wParam, lParam);
        switch (message)
        {
            case WmAppPublish: instance.AcceptPublish(); return 0;
            case WmAppDismiss: instance.AcceptDismissal(); return 0;
            case WmTimer: instance.Tick(); return 0;
            case WmClose: DestroyWindow(window); return 0;
            case WmDestroy: KillTimer(window, TimerId); PostQuitMessage(0); return 0;
            default: return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private enum Motion { Still, Entering, Exiting }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; public PointNative(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative { public int Width, Height; public SizeNative(int width, int height) { Width = width; Height = height; } }
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; public RectNative(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; public BlendFunction(byte op, byte flags, byte alpha, byte format) { BlendOp = op; BlendFlags = flags; SourceConstantAlpha = alpha; AlphaFormat = format; } }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Value; public nint WParam, LParam; public uint Time; public PointNative Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public WindowProc? WindowProcedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName; [MarshalAs(UnmanagedType.LPWStr)] public string ClassName; public nint SmallIcon; }
    private delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(int extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, ref PointNative destination, ref SizeNative size, nint sourceDc, ref PointNative source, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint AddFontMemResourceEx(nint fontData, uint dataLength, nint reserved, out uint fontCount);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(nint dc, string text, int count, ref RectNative rectangle, uint format);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string fileName, int iconIndex, out nint largeIcon, out nint smallIcon, uint iconCount);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
