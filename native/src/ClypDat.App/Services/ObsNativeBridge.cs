using System.Runtime.InteropServices;
using System.Text;

namespace ClypDat.App.Services;

public sealed class ObsNativeBridge
{
    private const string DllName = "ClypDat.ObsBridge";
    private static readonly object ResolverLock = new();
    private static bool _resolverRegistered;

    public ObsNativeBridge()
    {
        EnsureResolver();
    }

    public void Initialize(
        string runtimeFolder,
        int maxHeight,
        int frameRate,
        int durationSeconds,
        string chatProcessName,
        string microphoneDeviceId,
        string gameExeName,
        string gameWindowTitle,
        string gameWindowClass,
        string gameDisplayName)
    {
        var result = clypdat_obs_init(runtimeFolder, maxHeight, frameRate, durationSeconds, chatProcessName, microphoneDeviceId, gameExeName, gameWindowTitle, gameWindowClass, gameDisplayName);
        ThrowIfFailed(result);
    }

    public void StartReplayCapture()
    {
        ThrowIfFailed(clypdat_obs_start_replay_capture());
    }

    public void Stop()
    {
        ThrowIfFailed(clypdat_obs_stop());
    }

    public void SetCapturePaused(bool paused)
    {
        ThrowIfFailed(clypdat_obs_set_capture_paused(paused));
    }

    public string SaveReplay(string outputFolder)
    {
        var buffer = new StringBuilder(1024);
        var result = clypdat_obs_save_replay(outputFolder, buffer, buffer.Capacity);
        ThrowIfFailed(result);
        return buffer.ToString();
    }

    public void Shutdown()
    {
        clypdat_obs_shutdown();
    }

    public ObsCapturePath GetCapturePath() => (ObsCapturePath)Math.Clamp(clypdat_obs_capture_status(), 0, 2);

    public string GetActiveEncoder()
    {
        var buffer = new StringBuilder(128);
        clypdat_obs_active_encoder(buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static void ThrowIfFailed(int result)
    {
        if (result == 0) return;
        throw new InvalidOperationException(GetLastError());
    }

    private static string GetLastError()
    {
        var buffer = new StringBuilder(2048);
        clypdat_obs_last_error(buffer, buffer.Capacity);
        var message = buffer.ToString();
        return string.IsNullOrWhiteSpace(message) ? "OBS replay backend failed." : message;
    }

    private static void EnsureResolver()
    {
        lock (ResolverLock)
        {
            if (_resolverRegistered) return;
            var runtime = ObsRuntimeLocator.Locate();
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (Directory.Exists(runtime.BinFolder) && !path.Split(Path.PathSeparator).Contains(runtime.BinFolder, StringComparer.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", $"{runtime.BinFolder}{Path.PathSeparator}{path}");
            }

            NativeLibrary.SetDllImportResolver(typeof(ObsNativeBridge).Assembly, (libraryName, assembly, searchPath) =>
            {
                if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                return File.Exists(runtime.BridgePath) ? NativeLibrary.Load(runtime.BridgePath, assembly, searchPath) : IntPtr.Zero;
            });
            _resolverRegistered = true;
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int clypdat_obs_init(string runtimeFolder, int maxHeight, int frameRate, int durationSeconds, string chatProcessName, string microphoneDeviceId, string gameExeName, string gameWindowTitle, string gameWindowClass, string gameDisplayName);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int clypdat_obs_start_replay_capture();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int clypdat_obs_save_replay(string outputFolder, StringBuilder outputPath, int outputPathLength);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int clypdat_obs_set_capture_paused(bool paused);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int clypdat_obs_stop();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void clypdat_obs_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int clypdat_obs_capture_status();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void clypdat_obs_active_encoder(StringBuilder encoder, int encoderLength);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void clypdat_obs_last_error(StringBuilder message, int messageLength);
}

public enum ObsCapturePath
{
    NoFrames,
    WindowFallback,
    GameHook
}
