namespace Eve.App.Services;

/// <summary>
/// EVE bundles ffmpeg/ffprobe (see native/vendor/ffmpeg, copied to
/// {publish}/ffmpeg by Eve.App.csproj) so users don't need to install them
/// separately. Every call site in the app spawns "ffmpeg"/"ffprobe" by bare
/// name via ProcessStartInfo, relying on PATH resolution - so this just
/// makes sure the bundled copy is first on PATH, taking priority over any
/// system-installed ffmpeg, rather than rewriting every call site to use an
/// absolute path.
/// </summary>
public static class FfmpegPathResolver
{
    public static void EnsureBundledFfmpegOnPath()
    {
        var bundledFolder = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (!Directory.Exists(bundledFolder)) return;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = path.Split(Path.PathSeparator);
        if (entries.Contains(bundledFolder, StringComparer.OrdinalIgnoreCase)) return;

        Environment.SetEnvironmentVariable("PATH", $"{bundledFolder}{Path.PathSeparator}{path}");
    }
}
