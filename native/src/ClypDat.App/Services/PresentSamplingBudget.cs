namespace ClypDat.App.Services;

// Keeps the GPU crop/scale input fresh without phase-locking a source that
// presents at the same rate as the configured output. A one-frame credit is
// not enough: a present that arrives just before the pacing tick would be
// rejected, then the next tick would consume the older image and repeat the
// mistake forever. Two credits absorb that boundary jitter while keeping a
// faster source bounded near the requested output rate.
internal sealed class PresentSamplingBudget
{
    private const double MaximumCredits = 2.0;
    // TimeSpan's 100ns ticks cannot exactly represent common frame intervals
    // (notably 1/90 and 1/60).  Do not turn that rounding residue into a
    // periodic lost source frame when this budget runs at the producer.
    private const double CreditRoundingTolerance = 0.001;
    private double _credits = 1.0;
    private double _samplesPerSecond;
    private TimeSpan _lastRefill;

    public PresentSamplingBudget(int samplesPerSecond)
    {
        SetRate(samplesPerSecond, TimeSpan.Zero);
    }

    public void SetRate(int samplesPerSecond, TimeSpan now)
    {
        _samplesPerSecond = Math.Clamp(samplesPerSecond, 15, 240);
        _credits = 1.0;
        _lastRefill = now;
    }

    public bool TryConsume(TimeSpan now, bool pendingSample)
    {
        if (now < _lastRefill) _lastRefill = now;
        var elapsed = now - _lastRefill;
        _credits = Math.Min(MaximumCredits, _credits + elapsed.TotalSeconds * _samplesPerSecond);
        _lastRefill = now;

        // The first present after the pacing tick is mandatory. It is the only
        // candidate that can feed that tick when the source is not faster than
        // the target, so it is allowed to borrow one credit.
        if (!pendingSample && _credits < 1.0) _credits = 1.0;
        if (_credits < 1.0 - CreditRoundingTolerance) return false;

        _credits = Math.Max(0, _credits - 1.0);
        return true;
    }
}
