using System.Runtime.InteropServices;
using System.Text;

namespace Eve.App.Services;

public sealed class ObsNativeBridge
{
    private const string DllName = "Eve.ObsBridge";
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
        string gameWindowClass)
    {
        var result = eve_obs_init(runtimeFolder, maxHeight, frameRate, durationSeconds, chatProcessName, microphoneDeviceId, gameExeName, gameWindowTitle, gameWindowClass);
        ThrowIfFailed(result);
    }

    public void StartReplayCapture()
    {
        ThrowIfFailed(eve_obs_start_replay_capture());
    }

    public void Stop()
    {
        ThrowIfFailed(eve_obs_stop());
    }

    public void SetCapturePaused(bool paused)
    {
        ThrowIfFailed(eve_obs_set_capture_paused(paused));
    }

    public string SaveReplay(string outputFolder)
    {
        var buffer = new StringBuilder(1024);
        var result = eve_obs_save_replay(outputFolder, buffer, buffer.Capacity);
        ThrowIfFailed(result);
        return buffer.ToString();
    }

    public void Shutdown()
    {
        eve_obs_shutdown();
    }

    private static void ThrowIfFailed(int result)
    {
        if (result == 0) return;
        throw new InvalidOperationException(GetLastError());
    }

    private static string GetLastError()
    {
        var buffer = new StringBuilder(2048);
        eve_obs_last_error(buffer, buffer.Capacity);
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
    private static extern int eve_obs_init(string runtimeFolder, int maxHeight, int frameRate, int durationSeconds, string chatProcessName, string microphoneDeviceId, string gameExeName, string gameWindowTitle, string gameWindowClass);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int eve_obs_start_replay_capture();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int eve_obs_save_replay(string outputFolder, StringBuilder outputPath, int outputPathLength);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int eve_obs_set_capture_paused(bool paused);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int eve_obs_stop();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void eve_obs_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void eve_obs_last_error(StringBuilder message, int messageLength);
}
