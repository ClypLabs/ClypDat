using System.Diagnostics;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

internal static class TestFfmpeg
{
    // Runs the bundled ffmpeg and fails the test with its own error text.
    public static async Task Run(params string[] arguments)
    {
        using var process = new Process { StartInfo = new(FfmpegPathResolver.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-v"); process.StartInfo.ArgumentList.Add("error");
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
    }
}
