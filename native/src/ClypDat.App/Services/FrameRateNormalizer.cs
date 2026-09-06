namespace ClypDat.App.Services;

/// <summary>
/// Turns a container's measured frame rate into the rate the clip was actually
/// recorded at.
///
/// The number read off the file is <c>avg_frame_rate</c> - frames divided by
/// duration - so it is a mean, and a clip that held a steady 90fps but dropped
/// frames in a few places averages out at something like 79. Reporting that
/// verbatim reads as a 79fps clip, which it never was.
///
/// So:
/// <list type="bullet">
/// <item>A measurement sitting on a real rate is that rate. This is what makes
/// 30 read as 30 and 60 as 60 - a CFR 120fps capture measures 119.983, because
/// the frame durations do not divide the timebase evenly.</item>
/// <item>Otherwise the rate rounds UP to the next multiple of 15, as long as the
/// measurement got within <see cref="ClaimTargetRatio"/> of it. Losing a slice
/// of frames does not change what the capture was running at.</item>
/// <item>Below that the drop is too large to still claim the target, and the
/// rate rounds to the nearest multiple of 15 instead.</item>
/// </list>
/// </summary>
public static class FrameRateNormalizer
{
    /// <summary>
    /// How much of a multiple of 15 the measurement has to reach before the clip
    /// is still called that rate. At 0.85 a steady 90 that measured 79 is 90
    /// (79/90 = 0.88), while a 46 is not 60 (0.77) and lands on 45.
    /// </summary>
    private const double ClaimTargetRatio = 0.85;

    /// <summary>Within this much of a known rate, the measurement IS that rate.</summary>
    private const double ExactTolerance = 0.01;

    private const int Step = 15;
    private const int Ceiling = 240;

    /// <summary>
    /// Rates that are not multiples of 15 but are still real: film and broadcast
    /// cadences, which arrive on imported clips. Without these a 24fps import
    /// would be rounded to 30 and a 50fps one down to 45.
    ///
    /// The NTSC rates are deliberately absent. 59.94 is 60000/1001 and everything
    /// that plays it calls it 60, so it lands on 60 through the tolerance below,
    /// as 29.97 lands on 30 and 23.976 on 24.
    /// </summary>
    private static readonly double[] BroadcastRates = [24, 25, 48, 50, 100];

    public static double Normalize(double measured)
    {
        if (measured <= 0 || double.IsNaN(measured) || double.IsInfinity(measured)) return 0;
        // Nothing sensible to say about a rate past the ceiling; leave it alone
        // rather than inventing one.
        if (measured > Ceiling) return measured;

        var exact = NearestKnownRate(measured);
        if (exact > 0) return exact;

        var lower = Math.Floor(measured / Step) * Step;
        var upper = lower + Step;
        if (measured >= upper * ClaimTargetRatio) return upper;
        // Ties go up: a measurement exactly between two rates came down from the
        // higher one.
        return measured - lower < upper - measured ? lower : upper;
    }

    private static double NearestKnownRate(double measured)
    {
        var best = 0d;
        var bestDistance = double.MaxValue;
        foreach (var rate in KnownRates())
        {
            var distance = Math.Abs(measured - rate);
            if (distance > rate * ExactTolerance || distance >= bestDistance) continue;
            best = rate;
            bestDistance = distance;
        }
        return best;
    }

    private static IEnumerable<double> KnownRates()
    {
        for (var rate = Step; rate <= Ceiling; rate += Step) yield return rate;
        foreach (var rate in BroadcastRates) yield return rate;
    }
}
