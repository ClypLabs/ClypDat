using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClypDat.App.Services;

internal static class NativeRecordingVerification
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct Text16 { internal IntPtr Data; internal uint Length; internal uint Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct Bytes { internal IntPtr Data; internal uint Capacity; internal uint Required; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Verify(Text16 directory, Text16 ffmpeg, uint fps, uint variable, ref Bytes report);

    internal static int Run(string directory, int rounds = 1)
    {
        try
        {
            NativeRecorderSession.RequireCompleteEngine();
            var root = Path.GetFullPath(directory);
            if (rounds is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rounds));
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException("Verification output directory must be empty.");
            Directory.CreateDirectory(root);
            var verify = Marshal.GetDelegateForFunctionPointer<Verify>(
                NativeLibrary.GetExport(NativeRecorderLibrary.Handle, "cd_recorder_verify"));
            var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            var ffmpegPointer = Marshal.StringToHGlobalUni(ffmpeg);
            var reportPointer = Marshal.AllocHGlobal(65536);
            try
            {
                for (var round = 1; round <= rounds; round++)
                foreach (var fps in new uint[] { 30, 60, 90, 120 })
                foreach (var variable in new uint[] { 0, 1 })
                {
                    var scenario = $"round-{round}-{fps}-{(variable == 0 ? "CFR" : "VFR")}";
                    var fixture = Path.Combine(root, scenario);
                    var fixturePointer = Marshal.StringToHGlobalUni(fixture);
                    try
                    {
                        var report = new Bytes { Data = reportPointer, Capacity = 65536 };
                        var result = verify(new() { Data = fixturePointer, Length = checked((uint)fixture.Length) },
                            new() { Data = ffmpegPointer, Length = checked((uint)ffmpeg.Length) }, fps, variable, ref report);
                        if (report.Required > report.Capacity) throw new InvalidOperationException("Native verification report exceeded its bound.");
                        var bytes = new byte[report.Required];
                        Marshal.Copy(report.Data, bytes, 0, bytes.Length);
                        var json = Encoding.UTF8.GetString(bytes);
                        File.WriteAllText(Path.Combine(root, scenario + ".json"), json);
                        if (result != 0) throw new InvalidOperationException($"Native recording verification failed ({result}): {json}");
                        using var parsed = JsonDocument.Parse(json);
                        if (!parsed.RootElement.GetProperty("decoded").GetBoolean() || !parsed.RootElement.GetProperty("seeked").GetBoolean())
                            throw new InvalidOperationException("Native verification did not decode and seek its output.");
                    }
                    finally { Marshal.FreeHGlobal(fixturePointer); }
                }
            }
            finally { Marshal.FreeHGlobal(ffmpegPointer); Marshal.FreeHGlobal(reportPointer); }
            Console.WriteLine($"Native generated frame/PCM recording, saves, decode and seek verified for {rounds} round(s): {root}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
