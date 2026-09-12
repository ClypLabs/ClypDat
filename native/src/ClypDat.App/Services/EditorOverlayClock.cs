using System.Diagnostics;

namespace ClypDat.App.Services;

/// <summary>
/// Projects the editor's overlay time from the latest time libvlc accepted.
/// LibVLC 3 publishes TimeChanged sparsely, so the projection fills the gaps
/// without letting a stale callback run ahead forever.
/// </summary>
internal sealed class EditorOverlayClock
{
    private readonly object _gate = new();
    private readonly Func<long> _timestamp;
    private readonly Func<TimeSpan> _duration;
    private long _generation;
    private long _anchorTimestamp;
    private TimeSpan _anchorPosition;
    private double _rate = 1;
    private bool _hasAnchor;
    private bool _running;

    internal EditorOverlayClock(Func<TimeSpan> duration, Func<long>? timestamp = null)
    {
        _duration = duration;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    }

    internal void Reset(long generation, TimeSpan position, double rate)
    {
        lock (_gate)
        {
            _generation = generation;
            _anchorPosition = Clamp(position);
            _anchorTimestamp = _timestamp();
            _rate = NormalizeRate(rate);
            _hasAnchor = true;
            _running = false;
        }
    }

    internal void BeginSeek(long generation, TimeSpan requested, double rate)
    {
        lock (_gate)
        {
            _generation = generation;
            _anchorPosition = Clamp(requested);
            _anchorTimestamp = _timestamp();
            _rate = NormalizeRate(rate);
            _hasAnchor = false;
            _running = false;
        }
    }

    internal void Resume(long generation, TimeSpan confirmed, double rate)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            _anchorPosition = Clamp(confirmed);
            _anchorTimestamp = _timestamp();
            _rate = NormalizeRate(rate);
            _hasAnchor = true;
            _running = true;
        }
    }

    internal void Freeze(long generation, TimeSpan confirmed)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            _anchorPosition = Clamp(confirmed);
            _anchorTimestamp = _timestamp();
            _hasAnchor = true;
            _running = false;
        }
    }

    // Safe inside an event callback: it touches only this clock's state.
    internal void FreezeAtCurrent(long generation, long timestamp)
    {
        lock (_gate)
        {
            if (generation != _generation || !_hasAnchor) return;
            _anchorPosition = PositionAt(timestamp);
            _anchorTimestamp = timestamp;
            _running = false;
        }
    }

    internal void Sample(long generation, TimeSpan position, long timestamp)
    {
        lock (_gate)
        {
            if (generation != _generation || !_running) return;
            _anchorPosition = Clamp(position);
            _anchorTimestamp = timestamp;
            _hasAnchor = true;
        }
    }

    internal void Continue(long generation, long timestamp)
    {
        lock (_gate)
        {
            if (generation != _generation || !_hasAnchor) return;
            _anchorPosition = PositionAt(timestamp);
            _anchorTimestamp = timestamp;
            _running = true;
        }
    }

    internal void SetRate(long generation, double rate)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            var now = _timestamp();
            if (_hasAnchor) { _anchorPosition = PositionAt(now); _anchorTimestamp = now; }
            _rate = NormalizeRate(rate);
        }
    }

    internal bool TryGetOverlayPosition(out TimeSpan position)
    {
        lock (_gate)
        {
            if (!_hasAnchor) { position = default; return false; }
            position = _running ? PositionAt(_timestamp()) : _anchorPosition;
            return true;
        }
    }

    private TimeSpan PositionAt(long timestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(_anchorTimestamp, timestamp);
        var maximum = TimeSpan.FromMilliseconds(Math.Max(500, 500 / _rate));
        if (elapsed > maximum) elapsed = maximum;
        return Clamp(_anchorPosition + TimeSpan.FromTicks((long)(elapsed.Ticks * _rate)));
    }

    private TimeSpan Clamp(TimeSpan position)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        var duration = _duration();
        return duration > TimeSpan.Zero && position > duration ? duration : position;
    }

    private static double NormalizeRate(double rate) => double.IsFinite(rate) && rate > 0 ? rate : 1;
}
