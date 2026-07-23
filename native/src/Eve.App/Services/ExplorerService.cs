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
                var arguments = selectFile
                    ? $"/n,/select,\"{path}\""
                    : $"/n,\"{path}\"";

                Process.Start(new ProcessStartInfo("explorer.exe", arguments)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception error)
            {
                AppLog.Error($"Failed to open Explorer for '{path}'", error);
            }
        });
    }
}
