using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Health diagnostics and adaptive tuning need exact two-second measurements.
// The UI, on the other hand, benefits from a short median window so a single
// scheduling interval does not look like capture instability.
internal sealed class ReplayFrameRateDisplaySmoother
{
    private readonly RollingMedian _output = new();
    private readonly RollingMedian _unique = new();
    private string _encoder = string.Empty;
    private int _targetFrameRate;
    private string _timingMode = string.Empty;
    private bool _hasConfiguration;

    public (double OutputFrameRate, double UniqueFrameRate) Update(ReplayCaptureHealth health)
    {
        var timingMode = ReplayFrameTimingPolicy.Normalize(health.FrameRateMode);
        if (!_hasConfiguration ||
            !string.Equals(_encoder, health.Encoder, StringComparison.Ordinal) ||
            _targetFrameRate != health.TargetFrameRate ||
            !string.Equals(_timingMode, timingMode, StringComparison.Ordinal))
        {
            Reset();
            _encoder = health.Encoder;
            _targetFrameRate = health.TargetFrameRate;
            _timingMode = timingMode;
            _hasConfiguration = true;
        }

        var output = _output.Add(health.OutputFrameRate);
        if (health.UniqueFrameRate <= 0 || health.State is ReplayCaptureState.Stopped or ReplayCaptureState.Failed ||
            health.DegradeReason == ReplayDegradeReason.CaptureStall)
        {
            _unique.Reset();
            return (output, 0);
        }

        return (output, _unique.Add(health.UniqueFrameRate));
    }

    public void Reset()
    {
        _output.Reset();
        _unique.Reset();
        _encoder = string.Empty;
        _targetFrameRate = 0;
        _timingMode = string.Empty;
        _hasConfiguration = false;
    }

    private sealed class RollingMedian
    {
        private readonly double[] _samples = new double[3];
        private int _count;
        private int _next;

        public double Add(double value)
        {
            _samples[_next] = value;
            _next = (_next + 1) % _samples.Length;
            _count = Math.Min(_count + 1, _samples.Length);

            Span<double> ordered = stackalloc double[3];
            _samples.AsSpan(0, _count).CopyTo(ordered);
            ordered[.._count].Sort();
            return ordered[_count / 2];
        }

        public void Reset()
        {
            _count = 0;
            _next = 0;
        }
    }
}
