using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace ClypDat.App.Services;

// Capture-thread counters. Timestamp errors and discontinuities are independent
// WASAPI flags; neither is evidence of a disk stall by itself.
internal sealed class AudioCaptureDiagnostics(string source)
{
    private long _lastDrain = Stopwatch.GetTimestamp();
    private long _nextLog = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
    private long _maxDrainTicks, _maxCallbackTicks, _packets, _discontinuities, _timestampErrors;

    public void Drain()
    {
        var now = Stopwatch.GetTimestamp();
        _maxDrainTicks = Math.Max(_maxDrainTicks, now - _lastDrain);
        _lastDrain = now;
        if (now >= _nextLog) Log();
    }

    // A stream with nothing playing signals nothing, so the capture loop's wait
    // returns on its own timeout instead. That interval is not thread
    // starvation and must not be recorded as maxDrainMs - just re-base it.
    public void Idle()
    {
        _lastDrain = Stopwatch.GetTimestamp();
        if (_lastDrain >= _nextLog) Log();
    }

    public void Packet(AudioClientBufferFlags flags)
    {
        _packets++;
        if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) _discontinuities++;
        if ((flags & AudioClientBufferFlags.TimestampError) != 0) _timestampErrors++;
    }

    public void Callback(long started) => _maxCallbackTicks = Math.Max(_maxCallbackTicks, Stopwatch.GetTimestamp() - started);

    public void Log()
    {
        AppLog.Debug($"Audio native diag: {source}, packets={_packets}, discontinuities={_discontinuities}, timestampErrors={_timestampErrors}, maxDrainMs={_maxDrainTicks * 1000d / Stopwatch.Frequency:0.###}, maxCallbackMs={_maxCallbackTicks * 1000d / Stopwatch.Frequency:0.###}.");
        _maxDrainTicks = _maxCallbackTicks = _packets = _discontinuities = _timestampErrors = 0;
        _nextLog = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
    }
}
