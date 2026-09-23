using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

// Managed ownership/discovery policy surrounds the native probe/remux/validation
// operation. Cancelling a library refresh does not abandon footage recovery.
internal static class FullSessionRecovery
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> Pending = new(StringComparer.OrdinalIgnoreCase);
    internal static string Marker(string path) => path + ".interrupted";
    internal static async Task<bool> RecoverAsync(string path, CancellationToken token = default)
    {
        if (!Pending.TryGetValue(path, out var pending))
        {
            if (!File.Exists(Marker(path)) || RecordingFileOwnership.IsActive(path)) return false;
            pending = Pending.GetOrAdd(path, key => new Lazy<Task<bool>>(() => Task.Run(() => RecoverCore(key))));
        }
        var task = pending.Value;
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        finally { if (task.IsCompleted) Pending.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(path, pending)); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct Text { internal IntPtr Data; internal uint Length, Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct Config { internal uint Size, Version; internal Text Input, Ffmpeg; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RecoverDelegate(ref Config config, out uint recovered);
    private static readonly Lazy<RecoverDelegate> Recover = new(() => Marshal.GetDelegateForFunctionPointer<RecoverDelegate>(
        NativeLibrary.GetExport(NativeRecorderLibrary.Handle, "cd_recording_recover")));

    private static unsafe bool RecoverCore(string path)
    {
        try
        {
            using var ownership = RecordingFileOwnership.Acquire(path);
            var input = Path.GetFullPath(path);
            var ffmpeg = FfmpegPathResolver.FfmpegPath;
            fixed (char* inputPointer = input, ffmpegPointer = ffmpeg)
            {
                var config = new Config
                {
                    Size = (uint)Marshal.SizeOf<Config>(), Version = 3,
                    Input = new() { Data = (IntPtr)inputPointer, Length = (uint)input.Length },
                    Ffmpeg = new() { Data = (IntPtr)ffmpegPointer, Length = (uint)ffmpeg.Length }
                };
                if (Recover.Value(ref config, out var recovered) != 0 || recovered == 0) return false;
            }
            AppLog.Info($"Recovered interrupted Full Session: {path}.");
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"Full Session recovery deferred; original preserved: {path}.", error);
            return false;
        }
    }
}
