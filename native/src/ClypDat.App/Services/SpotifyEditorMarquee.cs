using LibVLCSharp.Shared;

namespace ClypDat.App.Services;

/// <summary>
/// Draws the clip's Spotify track over the editor's video, so the overlay can
/// be seen before it is exported.
///
/// Through libVLC's marquee rather than an Avalonia control on top of the
/// video: the video view is a native child window and paints over every
/// Avalonia sibling regardless of z-order, so a control laid over it would be
/// invisible exactly where it matters. The marquee is drawn by the video output
/// itself, which is the only thing that can be on top of it.
///
/// This is a preview, not the export. ffmpeg draws the real card - same text,
/// same corner, plus the translucent box behind it that libVLC's marquee has no
/// equivalent for.
/// </summary>
internal static class SpotifyEditorMarquee
{
    // libVLC composes the marquee position from a bitmask: 1 left, 2 right,
    // 4 top, 8 bottom, 0 centred.
    private const int Left = 1;
    private const int Right = 2;
    private const int Top = 4;
    private const int Bottom = 8;

    private const int Margin = 28;
    private const int FontSize = 26;
    private const int White = 0xFFFFFF;
    private const int Opaque = 255;

    /// <summary>Shows the track, or clears the marquee when there is nothing to show.</summary>
    public static void Apply(MediaPlayer? player, string? track, string? artist, TimeSpan? length, string? position, bool enabled)
    {
        if (player is null) return;

        var text = Compose(track, artist, length);
        if (!enabled || string.IsNullOrEmpty(text))
        {
            Clear(player);
            return;
        }

        try
        {
            player.SetMarqueeInt(VideoMarqueeOption.Enable, 1);
            player.SetMarqueeString(VideoMarqueeOption.Text, text);
            player.SetMarqueeInt(VideoMarqueeOption.Position, Placement(position));
            player.SetMarqueeInt(VideoMarqueeOption.X, Margin);
            player.SetMarqueeInt(VideoMarqueeOption.Y, Margin);
            player.SetMarqueeInt(VideoMarqueeOption.Size, FontSize);
            player.SetMarqueeInt(VideoMarqueeOption.Color, White);
            player.SetMarqueeInt(VideoMarqueeOption.Opacity, Opaque);
            // Zero is "until something says otherwise" - the default expires
            // after a few seconds, which for a preview reads as a bug.
            player.SetMarqueeInt(VideoMarqueeOption.Timeout, 0);
        }
        catch (Exception error)
        {
            // A preview is not worth taking playback down for.
            AppLog.Error("Spotify: could not draw the editor overlay.", error);
        }
    }

    public static void Clear(MediaPlayer? player)
    {
        if (player is null) return;
        try { player.SetMarqueeInt(VideoMarqueeOption.Enable, 0); }
        catch (Exception error) { AppLog.Error("Spotify: could not clear the editor overlay.", error); }
    }

    /// <summary>The same line ffmpeg burns in, so the preview is not a different overlay.</summary>
    public static string Compose(string? track, string? artist, TimeSpan? length)
    {
        if (string.IsNullOrWhiteSpace(track)) return string.Empty;
        var text = string.IsNullOrWhiteSpace(artist) ? track! : $"{track} - {artist}";
        return length is { } duration ? $"{text}  {(int)duration.TotalMinutes}:{duration.Seconds:00}" : text;
    }

    private static int Placement(string? position) => position switch
    {
        "Top Left" => Top | Left,
        "Top Right" => Top | Right,
        "Center Left" => Left,
        "Center Right" => Right,
        "Bottom Right" => Bottom | Right,
        _ => Bottom | Left
    };
}
