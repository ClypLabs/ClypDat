using System.Runtime.InteropServices;
using System.Text;

namespace ClypDat.App.Services;

/// <summary>
/// Library card thumbnails and editor filmstrips, decoded and encoded
/// in-process through ClypDat.Capture.Native (clypdat_clip_thumbnail.h).
/// Replaces ffmpeg.exe launches whose decoder spun up a thread per core for a
/// few pictures - the burst that made them unaffordable while a game ran.
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FilmstripConfiguration
    {
        public uint Size, Abi;
        public char* Path;
        public uint PathLength;
        public char* Output;
        public uint OutputLength;
        public long DurationUs;
        public uint FrameCount, FrameHeight, Quality, Background, Decoded, Reserved0;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WriteCall(Configuration* configuration, byte* error, uint capacity, out uint required);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FilmstripCall(FilmstripConfiguration* configuration, byte* error, uint capacity, out uint required);

    private static readonly Lazy<WriteCall?> Api = new(() => Export<WriteCall>("cd_clip_thumbnail_write"));
    private static readonly Lazy<FilmstripCall?> FilmstripApi = new(() => Export<FilmstripCall>("cd_clip_filmstrip_write"));

    private static T? Export<T>(string name) where T : Delegate
    {
        try
        {
            return NativeLibrary.TryGetExport(NativeRecorderLibrary.Handle, name, out var export)
                ? Marshal.GetDelegateForFunctionPointer<T>(export)
                : null;
        }
        catch (Exception error)
        {
            AppLog.Info($"Native clip images unavailable ({name}): {error.Message}");
            return null;
        }
    }

    internal static bool Available => Api.Value is not null;
    internal static bool FilmstripAvailable => FilmstripApi.Value is not null;

    /// <summary>Writes a strip of <paramref name="frameCount"/> frames; returns (decoded, null) or (0, reason).</summary>
    internal static (int Decoded, string? Failure) WriteFilmstrip(string path, string output, TimeSpan duration, int frameCount, int frameHeight, int quality, bool background)
    {
        if (FilmstripApi.Value is not { } write) return (0, "Native filmstrips unavailable.");
        var error = new byte[512];
        fixed (char* source = path)
        fixed (char* destination = output)
        fixed (byte* message = error)
        {
            var configuration = new FilmstripConfiguration
            {
                Size = (uint)sizeof(FilmstripConfiguration), Abi = 3,
                Path = source, PathLength = (uint)path.Length,
                Output = destination, OutputLength = (uint)output.Length,
                DurationUs = duration.Ticks / 10, FrameCount = (uint)frameCount, FrameHeight = (uint)frameHeight,
                Quality = (uint)quality, Background = background ? 1u : 0u
            };
            var result = write(&configuration, message, (uint)error.Length, out var required);
            if (result == 0) return ((int)configuration.Decoded, null);
            var length = Math.Min((int)required, error.Length - 1);
            return (0, length > 0 ? Encoding.UTF8.GetString(error, 0, length) : $"Native filmstrip failed ({result}).");
        }
    }

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
