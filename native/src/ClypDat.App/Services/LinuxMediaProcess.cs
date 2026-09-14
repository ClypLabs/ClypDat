using System.Diagnostics;

namespace ClypDat.App.Services;

internal static class LinuxMediaProcess
{
    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token, Action<string>? onOutput = null)
    {
        using var process = new Process { StartInfo = new(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        try
        {
            var output = onOutput is null ? process.StandardOutput.ReadToEndAsync(token) : ReadProgressAsync(process.StandardOutput, onOutput, token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            var detail = await error;
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} exited {process.ExitCode}: {detail}");
            return await output;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
    private static async Task<string> ReadProgressAsync(StreamReader reader, Action<string> output, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is { } line) output(line);
        return string.Empty;
    }
}
