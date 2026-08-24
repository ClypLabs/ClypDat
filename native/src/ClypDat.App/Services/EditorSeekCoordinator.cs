using System.Diagnostics;

namespace ClypDat.App.Services;

// Keeps the editor's video transport and external WASAPI output in a single,
// deliberately conservative state machine. LibVLC acknowledges a Time write
// before it has necessarily paused, landed, or begun presenting again, so none
// of those transitions may be inferred from Buffering/TimeChanged alone.
internal sealed class EditorSeekCoordinator
{
    internal static readonly TimeSpan PositionTolerance = TimeSpan.FromMilliseconds(150);
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _attemptTimeout;

    public EditorSeekCoordinator(TimeSpan? pollInterval = null, TimeSpan? attemptTimeout = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(10);
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<EditorSeekResult> SeekAsync(
        IEditorSeekTransport transport,
        TimeSpan target,
        bool resume,
        long generation,
        Func<bool> isCurrent,
        CancellationToken cancellationToken)
    {
        target = target < TimeSpan.Zero ? TimeSpan.Zero : target;
        if (!isCurrent()) return EditorSeekResult.SupersededResult;

        try
        {
            transport.StopAudio();
            TimeSpan landed = default;
            var landedSuccessfully = false;
            for (var attempt = 1; attempt <= 2 && !landedSuccessfully; attempt++)
            {
                if (!isCurrent()) return EditorSeekResult.SupersededResult;
                transport.PauseVideo();
                if (!await WaitUntilAsync(() => transport.IsPaused, isCurrent, cancellationToken).ConfigureAwait(false)) continue;

                if (!isCurrent()) return EditorSeekResult.SupersededResult;
                transport.WritePosition(target);
                if (!await WaitUntilAsync(
                        () => Math.Abs((transport.Position - target).TotalMilliseconds) <= PositionTolerance.TotalMilliseconds,
                        isCurrent,
                        cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                landed = transport.Position;
                landedSuccessfully = true;
            }

            if (!landedSuccessfully)
            {
                MakeSafe(transport, generation, target, "landing timeout");
                return EditorSeekResult.FailedResult;
            }

            if (!resume) return new EditorSeekResult(true, false, false, landed, default);

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (!isCurrent()) return EditorSeekResult.SupersededResult;
                transport.ResumeVideo();
                if (await WaitUntilAsync(
                        () => transport.Position - landed >= TimeSpan.FromMilliseconds(20),
                        isCurrent,
                        cancellationToken).ConfigureAwait(false))
                {
                    if (!isCurrent()) return EditorSeekResult.SupersededResult;
                    var anchor = transport.Position;
                    transport.AnchorAudio(anchor);
                    transport.StartAudio();
                    return new EditorSeekResult(true, true, false, landed, anchor);
                }

                if (!isCurrent()) return EditorSeekResult.SupersededResult;
                transport.PauseVideo();
            }

            MakeSafe(transport, generation, target, "roll timeout");
            return EditorSeekResult.FailedResult;
        }
        catch (OperationCanceledException)
        {
            MakeSafe(transport, generation, target, "cancelled");
            throw;
        }
    }

    private async Task<bool> WaitUntilAsync(Func<bool> predicate, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < _attemptTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isCurrent()) return false;
            if (predicate()) return true;
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return isCurrent() && predicate();
    }

    private static void MakeSafe(IEditorSeekTransport transport, long generation, TimeSpan target, string reason)
    {
        transport.StopAudio();
        transport.PauseVideo();
        AppLog.Debug($"Editor seek failed: reason={reason}, requested={target.TotalSeconds:0.###}s, generation={generation}.");
    }
}

internal interface IEditorSeekTransport
{
    bool IsPaused { get; }
    TimeSpan Position { get; }
    void StopAudio();
    void PauseVideo();
    void WritePosition(TimeSpan target);
    void ResumeVideo();
    void AnchorAudio(TimeSpan position);
    void StartAudio();
}

internal readonly record struct EditorSeekResult(bool Succeeded, bool Resumed, bool Superseded, TimeSpan Landed, TimeSpan AudioAnchor)
{
    public static EditorSeekResult FailedResult => new(false, false, false, default, default);
    public static EditorSeekResult SupersededResult => new(false, false, true, default, default);
}

// A small policy object keeps the device-clock conversion and correction
// hysteresis independently testable. It makes one correction at most per seek
// generation, after the output has had time to begin rendering real samples.
internal sealed class EditorAvClockPolicy
{
    private const double DriftThresholdMilliseconds = 150;
    private int _direction;
    private bool _corrected;
    private long _generation;

    public void Begin(long generation)
    {
        _generation = generation;
        _direction = 0;
        _corrected = false;
    }

    public bool TryGetCorrection(long generation, TimeSpan elapsed, TimeSpan audible, TimeSpan video, out TimeSpan correction)
    {
        correction = default;
        if (generation != _generation || _corrected || elapsed < TimeSpan.FromMilliseconds(250) || elapsed > TimeSpan.FromSeconds(1.5)) return false;

        var drift = video - audible;
        if (Math.Abs(drift.TotalMilliseconds) <= DriftThresholdMilliseconds)
        {
            _direction = 0;
            return false;
        }

        var direction = Math.Sign(drift.TotalMilliseconds);
        if (_direction != direction)
        {
            _direction = direction;
            return false;
        }

        _corrected = true;
        correction = video < TimeSpan.Zero ? TimeSpan.Zero : video;
        return true;
    }

    public static TimeSpan ToMediaTime(TimeSpan anchor, long anchorDevicePosition, long devicePosition, int bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return anchor;
        var elapsedBytes = Math.Max(0, devicePosition - anchorDevicePosition);
        return anchor + TimeSpan.FromSeconds(elapsedBytes / (double)bytesPerSecond);
    }
}
