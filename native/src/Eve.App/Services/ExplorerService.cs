using System.Diagnostics;

namespace Eve.App.Services;

public static class ExplorerService
{
    public static void Open(string path, bool selectFile)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        _ = Task.Run(() =>
        {
            try
            {
                var startInfo = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (selectFile) startInfo.ArgumentList.Add("/select,");
                startInfo.ArgumentList.Add(path);

                using var process = Process.Start(startInfo);
            }
            catch (Exception error)
            {
                AppLog.Error($"Failed to open Explorer for '{path}'", error);
            }
        });
    }
}
