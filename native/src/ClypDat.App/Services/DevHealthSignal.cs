using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public static class DevHealthSignal
{
    public static void SignalIfRequested()
    {
        if (!DevChannelMode.Enabled) return;
        var token = Environment.GetCommandLineArgs()
            .FirstOrDefault(argument => argument.StartsWith("--dev-health-token=", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(token)) return;
        token = token["--dev-health-token=".Length..];
        if (token.Length != 32 || !token.All(Uri.IsHexDigit)) return;
        try
        {
            var path = Path.Combine(AppDataPaths.Root, "health", token + ".ok");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { }
    }
}
