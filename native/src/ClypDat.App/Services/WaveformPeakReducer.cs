namespace ClypDat.App.Services;

/// <summary>
/// Fits stored waveform peaks to the width they are being drawn at.
///
/// Peaks are captured at the resolution the timeline lane reaches when it is
/// zoomed all the way in, so at lower zoom there are many more of them than the
/// lane has pixels. Reducing them here keeps the drawn geometry the same size
/// whatever the zoom, and zooming in simply stops discarding detail.
///
/// Deliberately not on the control: this is arithmetic, and the control's static
/// constructor needs a render backend that a test run has no reason to stand up.
/// </summary>
public static class WaveformPeakReducer
{
    /// <summary>
    /// One value per pixel column, each the loudest peak falling in it. Max
    /// rather than average: averaging smooths every transient away, and a drawn
    /// peak that never happened is worse than a coarse one. Peaks pass through
    /// unchanged when there are fewer of them than there are pixels, which is
    /// where a zoomed-in lane gets its detail from.
    /// </summary>
    public static double[] Reduce(IReadOnlyList<double> peaks, double width)
    {
        var columns = (int)Math.Ceiling(width);
        if (columns < 2 || peaks.Count <= columns)
        {
            var all = new double[peaks.Count];
            for (var index = 0; index < peaks.Count; index++) all[index] = Math.Clamp(peaks[index], 0, 1);
            return all;
        }

        var reduced = new double[columns];
        for (var index = 0; index < columns; index++)
        {
            var start = (int)((long)index * peaks.Count / columns);
            var end = (int)((long)(index + 1) * peaks.Count / columns);
            if (end <= start) end = start + 1;
            var loudest = 0d;
            for (var peak = start; peak < end && peak < peaks.Count; peak++)
            {
                var value = Math.Clamp(peaks[peak], 0, 1);
                if (value > loudest) loudest = value;
            }
            reduced[index] = loudest;
        }
        return reduced;
    }
}
