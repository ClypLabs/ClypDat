using System.Diagnostics;

namespace ClypDat.App.Services;

/// <summary>
/// ClypDat bundles ffmpeg/ffprobe (see native/vendor/ffmpeg, copied to
/// {publish}/ffmpeg by ClypDat.App.csproj) so users don't need to install them
/// separately. Call sites spawn them through <see cref="FfmpegPath"/> /
/// <see cref="FfprobePath"/>, which are absolute paths into that bundled folder.
///
/// Resolution is deliberately NOT search-based. With UseShellExecute = false and a
/// bare file name, .NET calls CreateProcess with lpApplicationName = NULL, whose
/// search order is: app directory, then CURRENT DIRECTORY, then System32, then
/// Windows, then PATH. PATH is last, and the bundled binaries live in a subfolder
/// of the app directory, so putting the bundled folder on PATH does not give it
/// priority - it leaves the current directory outranking it. Since no call site
/// sets WorkingDirectory, every child process inherited whatever directory the app
/// happened to be launched from, so an ffmpeg.exe sitting beside a double-clicked
/// clip would win. Absolute paths remove the search entirely.
/// </summary>
public static class FfmpegPathResolver
{
    /// <summary>Absolute path to the bundled ffmpeg.exe. Empty until <see cref="EnsureBundledFfmpeg"/> succeeds.</summary>
    public static string FfmpegPath { get; private set; } = string.Empty;

    /// <summary>Absolute path to the bundled ffprobe.exe. Empty until <see cref="EnsureBundledFfmpeg"/> succeeds.</summary>
    public static string FfprobePath { get; private set; } = string.Empty;

    /// <summary>Directory the child processes run in - never the caller's working directory.</summary>
    public static string WorkingDirectory => AppContext.BaseDirectory;

    public static bool IsAvailable => FfmpegPath.Length > 0 && FfprobePath.Length > 0;

    public static void EnsureBundledFfmpeg()
    {
        var bundledFolder = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        var ffmpeg = Path.Combine(bundledFolder, "ffmpeg.exe");
        var ffprobe = Path.Combine(bundledFolder, "ffprobe.exe");

        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
        {
            // Fail closed rather than falling back to a PATH lookup: a missing bundle
            // is a broken install, and searching for a replacement is exactly the
            // behaviour this class exists to prevent.
            AppLog.Error($"Bundled ffmpeg is missing from {bundledFolder}; media features are disabled.");
            return;
        }

        // NativeReplayBuffer P/Invokes libavcodec/libavformat directly (FFmpeg.AutoGen)
        // rather than shelling out, so it needs the shared DLLs' folder explicitly.
        FFmpeg.AutoGen.ffmpeg.RootPath = bundledFolder;

        FfmpegPath = ffmpeg;
        FfprobePath = ffprobe;
    }

    /// <summary>
    /// Maps the bare names "ffmpeg"/"ffprobe" onto the bundled absolute paths, and passes
    /// any other executable name through untouched. Helpers that take an executable name
    /// as a parameter route it through here so no ffmpeg child is ever resolved by search.
    /// </summary>
    public static string Resolve(string fileName)
    {
        if (string.Equals(fileName, "ffmpeg", StringComparison.OrdinalIgnoreCase) && FfmpegPath.Length > 0)
        {
            return FfmpegPath;
        }

        if (string.Equals(fileName, "ffprobe", StringComparison.OrdinalIgnoreCase) && FfprobePath.Length > 0)
        {
            return FfprobePath;
        }

        return fileName;
    }

    /// <summary>Builds a ProcessStartInfo for the bundled ffmpeg, pinned to an absolute path and a fixed working directory.</summary>
    public static ProcessStartInfo Ffmpeg() => Create(FfmpegPath, "ffmpeg");

    /// <summary>Builds a ProcessStartInfo for the bundled ffprobe, pinned to an absolute path and a fixed working directory.</summary>
    public static ProcessStartInfo Ffprobe() => Create(FfprobePath, "ffprobe");

    private static ProcessStartInfo Create(string exePath, string name)
    {
        if (exePath.Length == 0)
        {
            throw new InvalidOperationException(
                $"Bundled {name} was not resolved; EnsureBundledFfmpeg() must succeed before spawning it.");
        }

        return new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = WorkingDirectory,
        };
    }
}
