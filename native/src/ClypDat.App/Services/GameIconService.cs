using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClypDat.App.Services;

/// <summary>
/// Real per-game icons for the Library sidebar rail, pulled from the game's
/// own executable rather than shipped as assets - there's no bundled artwork
/// for an arbitrary game, and the exe icon is what the user already
/// associates with it from the taskbar.
///
/// Icons can only be extracted while the game is actually running (that's the
/// only time its executable path is known), so this caches to disk on
/// detection and every later lookup is a cache read. A game that has never
/// been seen running just has no icon and falls back to its initial badge.
/// </summary>
public static class GameIconService
{
    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClypDat",
        "game-icons");

    // Extraction is best-effort and never worth repeating in a session once
    // it has failed (protected process, no icon resource, access denied).
    private static readonly HashSet<string> Attempted = new(StringComparer.OrdinalIgnoreCase);

    private static string CachePathFor(string displayName)
    {
        var safe = string.Join("_", displayName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(CacheFolder, $"{safe}.png");
    }

    public static Bitmap? TryLoad(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        try
        {
            var path = CachePathFor(displayName);
            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch (Exception error)
        {
            AppLog.Error($"Game icon load failed for '{displayName}'", error);
            return null;
        }
    }

    /// <summary>
    /// Extracts and caches the icon for a detected game if it isn't cached
    /// yet. Safe to call on every detection tick - it short-circuits once the
    /// icon exists or extraction has already been tried this session.
    /// Returns true only when a new icon was written.
    /// </summary>
    public static bool EnsureCached(string displayName, int processId)
    {
        if (string.IsNullOrWhiteSpace(displayName) || processId <= 0) return false;
        lock (Attempted)
        {
            if (!Attempted.Add(displayName)) return false;
        }

        try
        {
            var cachePath = CachePathFor(displayName);
            if (File.Exists(cachePath)) return false;

            var exePath = ResolveExecutablePath(processId);
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                AppLog.Info($"Game icon: no executable path resolved for '{displayName}' (pid={processId}).");
                return false;
            }

            var bitmap = ExtractIconBitmap(exePath);
            if (bitmap is null)
            {
                AppLog.Info($"Game icon: no icon extracted for '{displayName}' from {exePath}.");
                return false;
            }

            Directory.CreateDirectory(CacheFolder);
            using (bitmap)
            {
                bitmap.Save(cachePath);
            }

            AppLog.Info($"Game icon cached for '{displayName}' from {exePath}.");
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"Game icon extraction failed for '{displayName}'", error);
            return false;
        }
    }

    // Process.MainModule throws for anything running at a higher integrity
    // level than us, which plenty of games do - QueryFullProcessImageName
    // against a limited-rights handle works where MainModule doesn't.
    private static string? ResolveExecutablePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == nint.Zero)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // Reads the exe's large icon into an Avalonia bitmap. Goes through the
    // icon's own 32bpp colour bitmap so alpha survives - drawing an HICON
    // into a DC and reading that back loses transparency.
    private static Bitmap? ExtractIconBitmap(string exePath)
    {
        var large = new nint[1];
        var small = new nint[1];
        if (ExtractIconEx(exePath, 0, large, small, 1) <= 0) return null;

        var icon = large[0] != nint.Zero ? large[0] : small[0];
        if (icon == nint.Zero) return null;

        try
        {
            if (!GetIconInfo(icon, out var info))
            {
                AppLog.Error($"Game icon: GetIconInfo failed for {exePath}", new InvalidOperationException($"win32={Marshal.GetLastWin32Error()}"));
                return null;
            }

            try
            {
                if (info.hbmColor == nint.Zero) return null;

                // Dimensions come from the bitmap handle itself rather than a
                // header-query pass of GetDIBits, which only reports them when
                // called with biBitCount zeroed and is easy to get subtly wrong.
                if (GetObject(info.hbmColor, Marshal.SizeOf<Win32Bitmap>(), out var bitmapInfo) == 0) return null;

                var width = bitmapInfo.bmWidth;
                var height = bitmapInfo.bmHeight;
                if (width <= 0 || height <= 0) return null;

                var header = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = width,
                    // Negative height requests top-down rows, matching the
                    // order Avalonia expects.
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                    biSizeImage = (uint)(width * height * 4)
                };

                var screenDc = GetDC(nint.Zero);
                if (screenDc == nint.Zero) return null;

                try
                {
                    var bgra = ReadBitmapPixels(screenDc, info.hbmColor, width, height, ref header);
                    if (bgra is null)
                    {
                        AppLog.Error($"Game icon: GetDIBits failed for {exePath}", new InvalidOperationException($"win32={Marshal.GetLastWin32Error()}"));
                        return null;
                    }

                    ApplyMaskAlphaIfNeeded(screenDc, info.hbmMask, bgra, width, height, ref header);

                    var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                    try
                    {
                        return new Bitmap(
                            PixelFormat.Bgra8888,
                            AlphaFormat.Unpremul,
                            handle.AddrOfPinnedObject(),
                            new PixelSize(width, height),
                            new Vector(96, 96),
                            width * 4);
                    }
                    finally
                    {
                        handle.Free();
                    }
                }
                finally
                {
                    ReleaseDC(nint.Zero, screenDc);
                }
            }
            finally
            {
                if (info.hbmColor != nint.Zero) DeleteObject(info.hbmColor);
                if (info.hbmMask != nint.Zero) DeleteObject(info.hbmMask);
            }
        }
        finally
        {
            if (large[0] != nint.Zero) DestroyIcon(large[0]);
            if (small[0] != nint.Zero && small[0] != large[0]) DestroyIcon(small[0]);
        }
    }

    private static byte[]? ReadBitmapPixels(nint dc, nint bitmap, int width, int height, ref BitmapInfoHeader header)
    {
        var buffer = new byte[width * height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return GetDIBits(dc, bitmap, 0, (uint)height, handle.AddrOfPinnedObject(), ref header, 0) == 0 ? null : buffer;
        }
        finally
        {
            handle.Free();
        }
    }

    // GDI routinely hands back icon colour bits with the alpha channel zeroed,
    // which would render the whole icon invisible. When that happens the
    // icon's AND mask is the real transparency source: black means opaque,
    // white means see-through. Only applied when every pixel came back fully
    // transparent, so genuinely alpha-blended icons are left untouched.
    private static void ApplyMaskAlphaIfNeeded(nint dc, nint maskBitmap, byte[] bgra, int width, int height, ref BitmapInfoHeader header)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0) return;
        }

        if (maskBitmap == nint.Zero)
        {
            // No mask to consult - a fully opaque icon beats an invisible one.
            for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
            return;
        }

        var mask = ReadBitmapPixels(dc, maskBitmap, width, height, ref header);
        if (mask is null)
        {
            for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
            return;
        }

        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, System.Text.StringBuilder exeName, ref int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ExtractIconEx(string file, int iconIndex, nint[] largeIcons, nint[] smallIcons, int icons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(nint dc, nint bitmap, uint startScan, uint scanLines, nint bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(nint handle, int size, out Win32Bitmap bitmap);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Bitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        // GetDIBits writes a colour table past the header for <=8bpp formats;
        // reserving it here keeps it from scribbling past the struct.
        public uint biColorTable0;
        public uint biColorTable1;
        public uint biColorTable2;
    }
}
