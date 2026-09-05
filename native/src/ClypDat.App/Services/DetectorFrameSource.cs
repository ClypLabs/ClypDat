namespace ClypDat.App.Services;

public sealed record GrayDetectorImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// Three HUD crops from one captured frame. The slots are deliberately unnamed:
/// what each one holds is decided per game by <see cref="DetectorRegions"/>, and
/// only that game's detector knows how to read them.
/// </summary>
public sealed record DetectorFrameSnapshot(
    DateTime CapturedUtc,
    GrayDetectorImage First,
    GrayDetectorImage Second,
    GrayDetectorImage Third);

public sealed record DetectorRegionSet(NormalizedRegion First, NormalizedRegion Second, NormalizedRegion Third);

public static class DetectorRegions
{
    // HELLDIVERS 2: centre banner ("ELIMINATED"), mission panel ("SQUAD
    // PAYOUT"), killstreak counter.
    private static readonly DetectorRegionSet Helldivers2 = new(
        new NormalizedRegion(0.34, 0.445, 0.32, 0.065),
        new NormalizedRegion(0.42, 0.335, 0.16, 0.055),
        new NormalizedRegion(0.45, 0.72, 0.12, 0.12));

    // Overwatch, measured from a 1920x1080 capture (see AutoClipResearch/
    // overwatch-2026-09-06):
    //
    //  First  - left column. Carries "PLAY OF THE GAME", "ELIMINATED BY" and
    //           "YOU ARE NOW DEATH SPECTATING". It is a tall strip rather than
    //           a box because the POTG banner moves vertically between matches
    //           (seen at y 0.05, 0.19 and 0.70 in a single session), while its
    //           x stays pinned to the left edge. The death/spectate lines share
    //           the top of the same column, so one crop covers all three.
    //  Second - kill feed strip. The streak label ("DOUBLE KILL" ... "QUINTUPLE
    //           KILL") sits at the top of the stack with the "<player> <damage>"
    //           elimination rows beneath it, so both events read from one crop.
    //  Third  - "TEAM KILL!", which unlike the POTG banner never moves.
    private static readonly DetectorRegionSet Overwatch = new(
        new NormalizedRegion(0.015, 0.02, 0.27, 0.80),
        new NormalizedRegion(0.43, 0.685, 0.28, 0.115),
        new NormalizedRegion(0.42, 0.20, 0.18, 0.055));

    // Fortnite, measured from real clips (see AutoClipResearch/
    // fortnite-2026-09-06):
    //
    //  First  - kill feed, centre-left. Full sentences rather than labels, so
    //           this one crop carries eliminations, who did them, and the
    //           "(64 m)" suffix Fortnite only prints on long-range kills.
    //  Second - the centred elimination banner: "ELIMINATION!", "DOUBLE ELIM!",
    //           "TRIPLE ELIM!", and "ENEMY TEAM WIPED!" stacked above it, so it
    //           needs room for three lines.
    //  Third  - upper centre, shared by "VICTORY ROYALE" and "ELIMINATED BY".
    //           They cannot co-occur - one is winning, the other is dying - and
    //           both live in the same third of the screen. It starts at the very
    //           top edge because the "ELIMINATED BY" label sits at y 15-42.
    private static readonly DetectorRegionSet Fortnite = new(
        new NormalizedRegion(0.01, 0.49, 0.33, 0.12),
        new NormalizedRegion(0.42, 0.66, 0.19, 0.16),
        new NormalizedRegion(0.30, 0.005, 0.39, 0.30));

    public static DetectorRegionSet? ForGame(string? gameId) => gameId?.ToLowerInvariant() switch
    {
        "helldivers2" => Helldivers2,
        "overwatch" => Overwatch,
        "fortnite" => Fortnite,
        _ => null
    };
}

public interface IDetectorFrameSource
{
    event EventHandler<DetectorFrameSnapshot>? DetectorFrameAvailable;

    /// <summary>
    /// Which game's HUD to crop for. Null stops the source producing frames at
    /// all, so a game with no detector costs nothing per captured frame.
    /// </summary>
    void SetDetectorRegions(DetectorRegionSet? regions);
}
