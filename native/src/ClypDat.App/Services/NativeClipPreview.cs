using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClypDat.App.Services;

/// <summary>
/// One hover preview decode, in-process through ClypDat.Capture.Native
/// (clypdat_clip_preview.h). Replaces an ffmpeg.exe process per hover whose raw
/// frames crossed a pipe. Stop may be called from any thread while another one
/// waits in <see cref="Take"/>; only the owner disposes.
/// </summary>
internal sealed unsafe class NativeClipPreview : SafeHandleZeroOrMinusOneIsInvalid
{
    private NativeClipPreview() : base(true) { }

    [StructLayout(LayoutKind.Sequential)]
    private struct Configuration
    {
        public uint Size, Abi;
        public char* Path;
        public uint PathLength, Paced;
        public long StartUs, DurationUs;
        public uint Width, Height, Fps, Reserved0;
        public int CropX, CropY, CropWidth, CropHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Frame
    {
        public uint Size, Abi;
        public ulong Sequence, FramesPerLoop, SourceBytesRead;
        public uint TimeoutMs, Finished;
    }

    internal readonly record struct TakeResult(ulong Sequence, ulong FramesPerLoop, long SourceBytesRead, bool Finished);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenCall(Configuration* configuration, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TakeCall(IntPtr handle, Frame* frame, byte* rgba, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int HandleCall(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ErrorCall(IntPtr handle, byte* utf8, uint capacity, out uint required);

    private sealed class Functions
    {
        public readonly OpenCall Open = Export<OpenCall>("cd_clip_preview_open");
        public readonly TakeCall Take = Export<TakeCall>("cd_clip_preview_take");
        public readonly HandleCall Stop = Export<HandleCall>("cd_clip_preview_stop");
        public readonly ErrorCall Error = Export<ErrorCall>("cd_clip_preview_error");
        public readonly HandleCall Release = Export<HandleCall>("cd_clip_preview_release");
        private static T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(NativeRecorderLibrary.Handle, name));
    }
    private static readonly Lazy<Functions> Api = new(() => new());

    /// <param name="crop">Saved crop edit in source pixels, or null to cover the canvas.</param>
    internal static NativeClipPreview Open(string path, TimeSpan start, TimeSpan duration, int width, int height, int fps,
        (int X, int Y, int Width, int Height)? crop, bool paced = true)
    {
        fixed (char* file = path)
        {
            var configuration = new Configuration
            {
                Size = (uint)sizeof(Configuration), Abi = 3, Path = file, PathLength = (uint)path.Length, Paced = paced ? 1u : 0u,
                StartUs = start.Ticks / 10, DurationUs = duration.Ticks / 10,
                Width = (uint)width, Height = (uint)height, Fps = (uint)fps,
                CropX = crop?.X ?? 0, CropY = crop?.Y ?? 0, CropWidth = crop?.Width ?? 0, CropHeight = crop?.Height ?? 0
            };
            var result = Api.Value.Open(&configuration, out var handle);
            if (result != 0) throw new InvalidOperationException($"Native clip preview could not open ({result}).");
            var preview = new NativeClipPreview();
            preview.SetHandle(handle);
            return preview;
        }
    }

    /// <summary>Copies the newest frame after <paramref name="after"/>; Sequence 0 means none arrived in time.</summary>
    internal TakeResult Take(ulong after, byte[] rgba, uint timeoutMs)
    {
        var added = false;
        try
        {
            DangerousAddRef(ref added);
            var frame = new Frame { Size = (uint)sizeof(Frame), Abi = 3, Sequence = after, TimeoutMs = timeoutMs };
            int result;
            fixed (byte* buffer = rgba) result = Api.Value.Take(handle, &frame, buffer, (uint)rgba.Length);
            if (result != 0) throw new InvalidOperationException(Error() is { Length: > 0 } error ? error : $"Native clip preview failed ({result}).");
            return new TakeResult(frame.Sequence, frame.FramesPerLoop,
                frame.SourceBytesRead > long.MaxValue ? long.MaxValue : (long)frame.SourceBytesRead, frame.Finished != 0);
        }
        finally { if (added) DangerousRelease(); }
    }

    /// <summary>Ends decoding and wakes a waiting Take. Safe after disposal.</summary>
    internal void Stop()
    {
        var added = false;
        try
        {
            DangerousAddRef(ref added);
            Api.Value.Stop(handle);
        }
        catch (ObjectDisposedException) { }
        finally { if (added) DangerousRelease(); }
    }

    internal string Error()
    {
        var added = false;
        try
        {
            DangerousAddRef(ref added);
            var result = Api.Value.Error(handle, null, 0, out var required);
            if (required == 0) return string.Empty;
            if (result != -6 || required > 64 * 1024) return $"Native clip preview error ({result}).";
            var bytes = new byte[required];
            fixed (byte* buffer = bytes)
            {
                result = Api.Value.Error(handle, buffer, required, out var written);
                return result == 0 ? Encoding.UTF8.GetString(bytes, 0, checked((int)written)) : $"Native clip preview error ({result}).";
            }
        }
        catch (ObjectDisposedException) { return string.Empty; }
        finally { if (added) DangerousRelease(); }
    }

    protected override bool ReleaseHandle()
    {
        try { return Api.Value.Release(handle) == 0; }
        catch { return false; }
    }
}
