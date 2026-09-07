namespace ClypDat.App.Services;

/// <summary>
/// Turns stored peaks into the heights a lane is drawn at.
///
/// Peaks are linear amplitude, and a lane drawn straight from them wastes
/// almost all of its height. Music sits around -30 dBFS, which is 0.03 linear:
/// a 40px Spotify lane got a 1px line and read as "nothing was captured" for a
/// track that plays through the whole clip.
///
/// So the lane is drawn on a decibel scale, which is the scale the loudness
/// actually lives on. -30 dBFS lands at half height instead of at 3%, and full
/// scale still draws full height.
///
/// Note what this deliberately is not: normalizing each lane against its own
/// loudest peak. That fills a quiet lane too - and it fills it completely.
/// Music holds a near-constant level, so dividing by the loudest peak in the
/// clip put every column of it within a few percent of the top and drew one
/// solid block of colour edge to edge, which says even less than the flat line
/// did. A fixed mapping keeps a steady source looking steady, at a height that
/// still means something, and keeps two lanes comparable to each other.
///
/// Deliberately not on the control, same as <see cref="WaveformPeakReducer"/>:
/// this is arithmetic, and the control's static constructor needs a render
/// backend that a test run has no reason to stand up.
/// </summary>
public static class WaveformLaneScale
{
    // Where the lane bottoms out. -60 dBFS is below anything anyone captured on
    // purpose - room tone through an open microphone sits around -55 - so a
    // silent lane still draws as silence rather than as amplified dither.
    public const double FloorDecibels = -60;

    /// <summary>One drawn height per pixel column, on a decibel scale.</summary>
    public static double[] Shape(IReadOnlyList<double> peaks, double width)
    {
        var columns = WaveformPeakReducer.Reduce(peaks, width);
        for (var index = 0; index < columns.Length; index++)
        {
            columns[index] = Height(columns[index]);
        }
        return columns;
    }

    /// <summary>
    /// One peak's drawn height, 0 at the floor and 1 at full scale. 0.5 is
    /// -30 dBFS, which is roughly where music mixed under a game sits.
    /// </summary>
    public static double Height(double peak)
    {
        if (peak <= 0) return 0;
        var decibels = 20 * Math.Log10(Math.Min(peak, 1));
        return decibels <= FloorDecibels ? 0 : (decibels - FloorDecibels) / -FloorDecibels;
    }
}
