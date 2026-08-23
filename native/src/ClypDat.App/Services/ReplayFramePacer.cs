namespace ClypDat.App.Services;

// The capture source and encoder run independently. This small state machine
// owns the output timeline so source stalls cannot make a CFR replay sparse.
internal sealed class ReplayFramePacer
{
    private readonly bool _variableFrameRate;
    private int _frameRate;
    private double _nextConstantPtsMicroseconds;
    private long _lastPtsMicroseconds = -1;

    public ReplayFramePacer(int frameRate, bool variableFrameRate)
    {
        _frameRate = Math.Clamp(frameRate, 30, 144);
        _variableFrameRate = variableFrameRate;
    }

    public void SetFrameRate(int frameRate) => _frameRate = Math.Clamp(frameRate, 30, 144);

    public long Next(TimeSpan elapsed, bool sourceAdvanced)
    {
        var intervalMicroseconds = 1_000_000.0 / _frameRate;
        long pts;
        if (_variableFrameRate)
        {
            var sourcePts = (long)Math.Round(elapsed.TotalMilliseconds * 1000);
            var gapPts = _lastPtsMicroseconds < 0
                ? sourcePts
                : _lastPtsMicroseconds + (long)Math.Round(intervalMicroseconds);
            pts = sourceAdvanced ? sourcePts : gapPts;
        }
        else
        {
            pts = (long)Math.Round(_nextConstantPtsMicroseconds);
            _nextConstantPtsMicroseconds += intervalMicroseconds;
        }

        // Some clocks only expose millisecond resolution. FFmpeg requires
        // strictly increasing timestamps even when two source changes occur
        // inside the same observed clock tick.
        pts = Math.Max(_lastPtsMicroseconds + 1, pts);
        _lastPtsMicroseconds = pts;
        return pts;
    }
}
