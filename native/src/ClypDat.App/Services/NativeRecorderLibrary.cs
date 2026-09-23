using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

internal static class NativeRecorderLibrary
{
    // Keep the engine and its dependencies loaded for every engine/SafeHandle
    // lifetime. Never alter process-wide search paths used by other libraries.
    private static readonly Lazy<IntPtr> Engine = new(() => LoadBundle(AppContext.BaseDirectory));

    internal static IntPtr Handle => Engine.Value;

    internal static IntPtr LoadBundle(string directory) => LoadBundle(directory, LoadRestricted, ReadVersion, NativeLibrary.Free);

    internal static IntPtr LoadBundle(string directory, Func<string, IntPtr> load,
        Func<IntPtr, string, uint> version, Action<IntPtr> release)
    {
        var root = Path.GetFullPath(directory);
        var handles = new List<IntPtr>();
        try
        {
            // Dependency order matters: avcodec/avformat import avutil and
            // swresample. All five must be verified before loading the engine.
            (string Name, string Export, uint Version)[] dependencies =
            [
                ("avutil-60.dll", "avutil_version", PackVersion(60, 26, 102)),
                ("swresample-6.dll", "swresample_version", PackVersion(6, 3, 102)),
                ("swscale-9.dll", "swscale_version", PackVersion(9, 5, 102)),
                ("avcodec-62.dll", "avcodec_version", PackVersion(62, 28, 102)),
                ("avformat-62.dll", "avformat_version", PackVersion(62, 12, 102))
            ];
            foreach (var dependency in dependencies)
            {
                var path = Path.Combine(root, "ffmpeg", dependency.Name);
                var handle = load(path);
                if (handle == IntPtr.Zero) throw new DllNotFoundException($"Native recorder component could not load: {path}. Reinstall ClypDat.");
                handles.Add(handle);
                var actual = version(handle, dependency.Export);
                if (actual != dependency.Version)
                    throw new BadImageFormatException($"Native recorder runtime mismatch: {path}; expected {dependency.Version:X6}, found {actual:X6}. Reinstall ClypDat.");
            }
            var enginePath = Path.Combine(root, "ClypDat.Capture.Native.dll");
            var engine = load(enginePath);
            if (engine == IntPtr.Zero) throw new DllNotFoundException($"Native recorder component could not load: {enginePath}. Reinstall ClypDat.");
            return engine;
        }
        catch
        {
            for (var index = handles.Count - 1; index >= 0; --index) release(handles[index]);
            throw;
        }
    }

    private static uint PackVersion(uint major, uint minor, uint micro) => major << 16 | minor << 8 | micro;

    private static IntPtr LoadRestricted(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The native recorder requires Windows.");
        const uint loadLibrarySearchDllLoadDir = 0x100;
        const uint loadLibrarySearchSystem32 = 0x800;
        var handle = LoadLibraryExW(path, IntPtr.Zero, loadLibrarySearchDllLoadDir | loadLibrarySearchSystem32);
        if (handle == IntPtr.Zero)
            throw new DllNotFoundException($"Native recorder component could not load: {path}. {new Win32Exception(Marshal.GetLastWin32Error()).Message}. Reinstall ClypDat.");
        return handle;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VersionFunction();

    private static uint ReadVersion(IntPtr handle, string export) =>
        Marshal.GetDelegateForFunctionPointer<VersionFunction>(NativeLibrary.GetExport(handle, export))();

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);
}
