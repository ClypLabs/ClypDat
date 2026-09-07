namespace ClypDat.App.Services;

/// <summary>
/// Turns stored peaks into the heights a lane is drawn at.
///
/// Peaks are linear amplitude, and a lane drawn straight from them wastes
/// almost all of its height. Music sits around -30 dBFS, which is 0.03 linear:
/// a 40px Spotify lane got a 1px line and read as "nothing was captured" for a
/// track that plays through the whole clip.
///
/// Two steps fix that. The lane is normalized against its own loudest peak, so
/// a quiet source fills the lane it was given, and what is left is put through
/// a perceptual curve so the quiet passages inside a loud lane lift too.
///
/// The cost is that lane height no longer compares between lanes - a full
/// Spotify lane can be 20 dB under a full game lane. That is the right trade
/// for what these lanes are read for, which is where in the clip a source is
/// making noise, not which source is loudest. Normalization is per lane and not
/// per view, so a lane's shape does not shift while the timeline is scrubbed
/// or zoomed.
///
/// Deliberately not on the control, same as <see cref="WaveformPeakReducer"/>:
/// this is arithmetic, and the control's static constructor needs a render
/// backend that a test run has no reason to stand up.
/// </summary>
public static class WaveformLaneScale
{
    // Below this the lane is silence - room tone, a muted app, a Discord lane
    // nobody spoke on. Amplifying it would draw dither as though it were
    // content, so a lane this quiet is left flat and reads as empty, which it
    // is.
    public const double SilenceFloor = 0.005;

    // A cap, because normalization on a nearly silent lane is division by
    // nearly nothing. Eight is ~18 dB: enough for music mixed well under the
    // game, short of turning a -50 dBFS lane into a full-height block.
    public const double MaximumGain = 8;

    // Normalizing to exactly 1 puts the loudest sample on the lane's edge with
    // no margin, and the geometry is drawn from the middle outwards - the tips
    // clip against the lane border.
    public const double TargetFill = 0.95;

    // Amplitude is not loudness. A 0.5 peak is 6 dB down, not half as loud, and
    // drawn linearly the whole quiet half of a lane crowds into the middle.
    // 0.6 is between plain amplitude and a square root, which lifts the quiet
    // detail without flattening the loud end into one solid bar.
    private const double CurveExponent = 0.6;

    /// <summary>One drawn height per pixel column, gained and curved.</summary>
    public static double[] Shape(IReadOnlyList<double> peaks, double width)
    {
        var columns = WaveformPeakReducer.Reduce(peaks, width);
        var gain = Gain(columns);

        for (var index = 0; index < columns.Length; index++)
        {
            columns[index] = Curve(Math.Min(1, columns[index] * gain));
        }
        return columns;
    }

    /// <summary>
    /// What the lane is multiplied by. 1 for a lane already at full scale and
    /// for one that holds nothing but silence.
    /// </summary>
    public static double Gain(IReadOnlyList<double> columns)
    {
        var loudest = 0d;
        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index] > loudest) loudest = columns[index];
        }

        if (loudest < SilenceFloor) return 1;
        return Math.Clamp(TargetFill / loudest, 1, MaximumGain);
    }

    public static double Curve(double value) => Math.Pow(Math.Clamp(value, 0, 1), CurveExponent);
}
