using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClypDat.App.Services;

internal sealed unsafe class NativeCameraPreview : SafeHandleZeroOrMinusOneIsInvalid
{
    private NativeCameraPreview() : base(true) { }
    private ulong _lastSequence;
    [StructLayout(LayoutKind.Sequential)]
    private struct Configuration
    {
        public uint Size, Abi;
        public char* Ffmpeg;
        public uint FfmpegLength, Reserved0;
        public char* Root;
        public uint RootLength, Reserved1;
        public long QpcAnchor, QpcFrequency;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Frame
    {
        public uint Size, Abi, Width, Height, Stride, RequiredBytes;
        public ulong Sequence;
        public long TimestampQpc;
        public uint Running, Reserved;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateCall(Configuration* configuration, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StartCall(IntPtr handle, char* moniker, uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CopyCall(IntPtr handle, Frame* frame, byte* pixels, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ErrorCall(IntPtr handle, byte* utf8, uint capacity, out uint required);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseCall(IntPtr handle);
    private sealed class Functions
    {
        public readonly CreateCall Create = Export<CreateCall>("cd_camera_preview_create");
        public readonly StartCall Start = Export<StartCall>("cd_camera_preview_start");
        public readonly CopyCall Copy = Export<CopyCall>("cd_camera_preview_copy");
        public readonly ErrorCall Error = Export<ErrorCall>("cd_camera_preview_error");
        public readonly ReleaseCall Release = Export<ReleaseCall>("cd_camera_preview_release");
        private static T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(NativeRecorderLibrary.Handle, name));
    }
    private static readonly Lazy<Functions> Api = new(() => new());
    internal static NativeCameraPreview Create()
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        var root = AppContext.BaseDirectory; // Preview creates no files.
        fixed (char* executable = ffmpeg, directory = root)
        {
            var configuration = new Configuration
            {
                Size = (uint)sizeof(Configuration), Abi = 3, Ffmpeg = executable, FfmpegLength = (uint)ffmpeg.Length,
                Root = directory, RootLength = (uint)root.Length, QpcAnchor = Stopwatch.GetTimestamp(), QpcFrequency = Stopwatch.Frequency
            };
            var result = Api.Value.Create(&configuration, out var handle);
            if (result != 0) throw new InvalidOperationException($"Native camera preview could not initialize ({result}). Reinstall ClypDat if native components are incompatible.");
            var preview = new NativeCameraPreview(); preview.SetHandle(handle); return preview;
        }
    }
    internal void Start(string moniker)
    {
        _lastSequence = 0;
        fixed (char* value = moniker) Check(Api.Value.Start(handle, value, (uint)moniker.Length));
        GC.KeepAlive(this);
    }
    internal Frame Copy(byte[] pixels)
    {
        var frame = new Frame { Size = (uint)sizeof(Frame), Abi = 3, Sequence = _lastSequence };
        fixed (byte* buffer = pixels) Check(Api.Value.Copy(handle, &frame, buffer, (uint)pixels.Length));
        if (frame.RequiredBytes > 0) _lastSequence = frame.Sequence;
        GC.KeepAlive(this); return frame;
    }
    internal string Error()
    {
        var result = Api.Value.Error(handle, null, 0, out var required);
        if (required == 0) { GC.KeepAlive(this); return string.Empty; }
        if (result != -6 || required > 4 * 1024 * 1024) return $"Native camera preview error ({result}).";
        var bytes = new byte[required];
        fixed (byte* buffer = bytes)
        {
            result = Api.Value.Error(handle, buffer, required, out var written);
            GC.KeepAlive(this);
            return result == 0 ? Encoding.UTF8.GetString(bytes, 0, checked((int)written)) : $"Native camera preview error ({result}).";
        }
    }
    private void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException(Error() is { Length: > 0 } error ? error : $"Native camera preview failed ({result}).");
    }
    protected override bool ReleaseHandle()
    {
        try
        {
            var result = Api.Value.Release(handle);
            if (result != 0) AppLog.Debug($"Native camera preview retained a live capture owner ({result}); restart ClypDat.");
            return true;
        }
        catch { return false; }
    }
}
