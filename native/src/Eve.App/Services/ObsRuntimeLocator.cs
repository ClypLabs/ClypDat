namespace Eve.App.Services;

public sealed record ObsRuntimeInfo(string RootFolder, string BinFolder, string BridgePath);

public static class ObsRuntimeLocator
{
    private const string BridgeFileName = "Eve.ObsBridge.dll";

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

        reason = string.Empty;
        return true;
    }
}
