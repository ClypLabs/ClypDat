namespace ClypDat.Core.Settings;

// Audio codec of replay clips and full-session recordings. The native recorder
// encodes it in memory; AAC is the default.
public static class RecordingAudioCodec
{
    public const string Opus = "Opus";
    public const string Aac = "AAC";
    public const string Vorbis = "Vorbis";

    public static string Normalize(string? value) =>
        string.Equals(value, Opus, StringComparison.OrdinalIgnoreCase) ? Opus :
        string.Equals(value, Vorbis, StringComparison.OrdinalIgnoreCase) ? Vorbis : Aac;

    // MP4 has no Vorbis mapping, so Vorbis recordings are always Matroska.
    public static bool RequiresMatroska(string? value) => Normalize(value) == Vorbis;

    public static string ClipExtension(string? codec) => RequiresMatroska(codec) ? "mkv" : "mp4";

    public static string FullSessionExtension(string? codec, string? container) =>
        RequiresMatroska(codec) ? "mkv" : FullSessionFormat.Extension(container);
}
