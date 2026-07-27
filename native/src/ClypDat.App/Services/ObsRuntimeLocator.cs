namespace ClypDat.App.Services;

public sealed record ObsRuntimeInfo(string RootFolder, string BinFolder, string BridgePath);

public static class ObsRuntimeLocator
{
    private const string BridgeFileName = "ClypDat.ObsBridge.dll";
    private static readonly string[] RequiredRuntimeFiles =
    {
        Path.Combine("bin", "64bit", "obs.dll"),
        Path.Combine("bin", "64bit", "libobs-d3d11.dll"),
        Path.Combine("bin", "64bit", "obs-ffmpeg-mux.exe"),
        Path.Combine("obs-plugins", "64bit", "win-capture.dll"),
        Path.Combine("obs-plugins", "64bit", "win-wasapi.dll"),
        Path.Combine("obs-plugins", "64bit", "obs-ffmpeg.dll")
    };

    internal static IReadOnlyList<string> RequiredFiles => RequiredRuntimeFiles;

    public static ObsRuntimeInfo Locate()
    {
        var appFolder = AppContext.BaseDirectory;
        var root = Path.Combine(appFolder, "obs");
        return new ObsRuntimeInfo(root, Path.Combine(root, "bin", "64bit"), Path.Combine(root, BridgeFileName));
    }

    public static bool IsAvailable(out ObsRuntimeInfo runtime, out string reason)
    {
        runtime = Locate();
        if (!Directory.Exists(runtime.RootFolder))
        {
            reason = $"OBS runtime folder missing: {runtime.RootFolder}";
            return false;
        }

        if (!File.Exists(runtime.BridgePath))
        {
            reason = $"OBS bridge missing: {runtime.BridgePath}";
            return false;
        }

        foreach (var relativePath in RequiredRuntimeFiles)
        {
            var path = Path.Combine(runtime.RootFolder, relativePath);
            if (File.Exists(path)) continue;
            reason = $"OBS runtime incomplete: missing {path}";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
