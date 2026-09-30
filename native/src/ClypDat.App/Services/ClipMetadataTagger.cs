namespace ClypDat.App.Services;

public static class ClipMetadataTagger
{
    // MP4/mov muxers only persist a whitelisted set of format-level metadata keys
    // (title, comment, artist, etc.) and silently drop arbitrary custom keys -
    // confirmed by directly probing a tagged file and finding the custom key gone.
    // "comment" is one of the recognized keys, so the backend name is embedded
    // inside it instead, prefixed for unambiguous parsing on read-back.
    // New clips use CLYPDAT_CAPTURE_BACKEND. MediaProbeService also accepts
    // LegacyBackendTagKey so existing EVE-era clips keep their label.
    public const string BackendTagKey = "CLYPDAT_CAPTURE_BACKEND";
    internal const string LegacyBackendTagKey = "EVE_CAPTURE_BACKEND";
}
