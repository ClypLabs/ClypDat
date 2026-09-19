namespace ClypDat.App.Services;

internal static class MediaContainerOptions
{
    internal static bool IsMatroska(string path) => string.Equals(Path.GetExtension(path), ".mkv", StringComparison.OrdinalIgnoreCase);
    internal static string RemuxOptions(string path) => IsMatroska(path) ? "" : "-movflags +faststart+use_metadata_tags";
    internal static void AddFinalizedOptions(ICollection<string> args, string path)
    {
        if (IsMatroska(path)) return;
        args.Add("-movflags"); args.Add("+faststart+use_metadata_tags");
    }
}
