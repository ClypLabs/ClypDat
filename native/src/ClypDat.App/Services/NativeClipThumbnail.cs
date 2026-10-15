using System.Runtime.InteropServices;
using System.Text;

namespace ClypDat.App.Services;

/// <summary>
/// One library card thumbnail, decoded and encoded in-process through
/// ClypDat.Capture.Native (clypdat_clip_thumbnail.h). Replaces an ffmpeg.exe
/// launch whose decoder spun up a thread per core for a single picture - the
/// burst that made thumbnails unaffordable while a game was running.
/// </summary>
internal static unsafe class NativeClipThumbnail
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Configuration
    {
        public uint Size, Abi;
        public char* Path;
        public uint PathLength;
        public char* Output;
        public uint OutputLength;
        public long AtUs;
        public uint Width, Quality, Background, Reserved0;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WriteCall(Configuration* configuration, byte* error, uint capacity, out uint required);

    private static readonly Lazy<WriteCall?> Api = new(() =>
    {
        try
        {
            return NativeLibrary.TryGetExport(NativeRecorderLibrary.Handle, "cd_clip_thumbnail_write", out var export)
                ? Marshal.GetDelegateForFunctionPointer<WriteCall>(export)
                : null;
        }
        catch (Exception error)
        {
            AppLog.Info($"Native thumbnails unavailable: {error.Message}");
            return null;
        }
    });

    internal static bool Available => Api.Value is not null;

    /// <summary>Writes <paramref name="output"/>; returns null on success or the failure reason.</summary>
    /// <param name="background">Run at background CPU, I/O and memory priority (a game is running).</param>
    internal static string? Write(string path, string output, TimeSpan at, int width, int quality, bool background)
    {
        if (Api.Value is not { } write) return "Native thumbnails unavailable.";
        var error = new byte[512];
        fixed (char* source = path)
        fixed (char* destination = output)
        fixed (byte* message = error)
        {
            var configuration = new Configuration
            {
                Size = (uint)sizeof(Configuration), Abi = 3,
                Path = source, PathLength = (uint)path.Length,
                Output = destination, OutputLength = (uint)output.Length,
                AtUs = at.Ticks / 10, Width = (uint)width, Quality = (uint)quality, Background = background ? 1u : 0u
            };
            var result = write(&configuration, message, (uint)error.Length, out var required);
            if (result == 0) return null;
            var length = Math.Min((int)required, error.Length - 1);
            return length > 0 ? Encoding.UTF8.GetString(error, 0, length) : $"Native thumbnail failed ({result}).";
        }
    }
}
