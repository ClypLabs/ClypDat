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
    private readonly Func<double>? _rate;

    public EditorSeekCoordinator(TimeSpan? pollInterval = null, TimeSpan? attemptTimeout = null, Func<double>? rate = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(10);
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromMilliseconds(500);
        _rate = rate;
    }

    /// <summary>
    /// The confirmation budget for one wait, scaled by the playback rate.
    /// </summary>
    /// <remarks>
    /// Every predicate here watches the transport's Position, and libvlc
    /// republishes that from its input loop, whose wake-ups are media-clocked.
    /// The same ~250ms of media therefore costs 500ms of wall at 0.5x and a full
    /// second at 0.25x, so a fixed budget is really a budget of FRAMES: at slow
    /// rates it expires before the first update can arrive, and MakeSafe then
    /// stops the audio and pauses the video on a seek that was about to land.
    /// Scaling leaves 1x and every faster rate exactly as they were, and the
    /// lower clamp bounds how long a genuinely failing seek can take.
    /// </remarks>
    private TimeSpan AttemptTimeout()
    {
        var rate = _rate?.Invoke() ?? 1.0;
        if (double.IsNaN(rate) || double.IsInfinity(rate)) rate = 1.0;
        return _attemptTimeout / Math.Clamp(rate, 0.25, 1.0);
    }

    public async Task<EditorSeekResult> SeekAsync(
        IEditorSeekTransport transport,
        TimeSpan target,
        bool resume,
        long generation,
        Func<bool> isCurrent,
        CancellationToken cancellationToken,
        bool resetBeforeSeek = false)
    {
        target = target < TimeSpan.Zero ? TimeSpan.Zero : target;
        if (!isCurrent()) return EditorSeekResult.SupersededResult;

        try
        {
            transport.StopAudio();
            if (resetBeforeSeek)
            {
                AppLog.Debug($"Editor seek proactive decoder reset: requested={target.TotalSeconds:0.###}s, generation={generation}.");
                if (!await ResetAndConfirmPausedAsync(transport, isCurrent, cancellationToken).ConfigureAwait(false))
                {
                    if (!isCurrent()) return EditorSeekResult.SupersededResult;
                    MakeSafe(transport, generation, target, "proactive reset timeout");
                    return EditorSeekResult.FailedResult;
                }
            }

            var result = await TrySeekSequenceAsync(transport, target, resume, isCurrent, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded || result.Superseded) return result.Result;

            AppLog.Debug($"Editor seek recovery reset: requested={target.TotalSeconds:0.###}s, reason={result.FailureReason}, generation={generation}.");
            if (!await ResetAndConfirmPausedAsync(transport, isCurrent, cancellationToken).ConfigureAwait(false))
            {
                if (!isCurrent()) return EditorSeekResult.SupersededResult;
                MakeSafe(transport, generation, target, "recovery reset timeout");
                return EditorSeekResult.FailedResult;
            }

            result = await TrySeekSequenceAsync(transport, target, resume, isCurrent, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded || result.Superseded) return result.Result;
            MakeSafe(transport, generation, target, $"recovery {result.FailureReason}");
            return EditorSeekResult.FailedResult;
        }
        catch (OperationCanceledException)
        {
            MakeSafe(transport, generation, target, "cancelled");
            throw;
        }
    }

    private async Task<SeekAttempt> TrySeekSequenceAsync(
        IEditorSeekTransport transport,
        TimeSpan target,
        bool resume,
        Func<bool> isCurrent,
        CancellationToken cancellationToken)
    {
        TimeSpan landed = default;
        var landedSuccessfully = false;
        for (var attempt = 1; attempt <= 2 && !landedSuccessfully; attempt++)
        {
            if (!isCurrent()) return SeekAttempt.SupersededResult;
            transport.PauseVideo();
            if (!await WaitUntilAsync(() => transport.IsPaused, isCurrent, cancellationToken).ConfigureAwait(false)) continue;

            if (!isCurrent()) return SeekAttempt.SupersededResult;
            transport.WritePosition(target);
            if (!await WaitUntilAsync(
                    () => Math.Abs((transport.Position - target).TotalMilliseconds) <= PositionTolerance.TotalMilliseconds,
                    isCurrent,
                    cancellationToken).ConfigureAwait(false)) continue;

            landed = transport.Position;
            landedSuccessfully = true;
        }

        if (!landedSuccessfully) return SeekAttempt.LandingFailed;
        if (!resume) return new SeekAttempt(new EditorSeekResult(true, false, false, landed, default), string.Empty);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (!isCurrent()) return SeekAttempt.SupersededResult;
            transport.ResumeVideo();
            if (await WaitUntilAsync(
                    () => transport.Position - landed >= TimeSpan.FromMilliseconds(20),
                    isCurrent,
                    cancellationToken).ConfigureAwait(false))
            {
                if (!isCurrent()) return SeekAttempt.SupersededResult;
                var anchor = transport.Position;
                transport.AnchorAudio(anchor);
                transport.StartAudio();
                return new SeekAttempt(new EditorSeekResult(true, true, false, landed, anchor), string.Empty);
            }

            if (!isCurrent()) return SeekAttempt.SupersededResult;
            transport.PauseVideo();
        }

        return SeekAttempt.RollFailed;
    }

    private async Task<bool> ResetAndConfirmPausedAsync(IEditorSeekTransport transport, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        if (!isCurrent()) return false;
        transport.ResetVideo();
        return await WaitUntilAsync(() => transport.IsPaused, isCurrent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WaitUntilAsync(Func<bool> predicate, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        var timeout = AttemptTimeout();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
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
    void ResetVideo();
    void WritePosition(TimeSpan target);
    void ResumeVideo();
    void AnchorAudio(TimeSpan position);
    void StartAudio();
}

internal readonly record struct SeekAttempt(EditorSeekResult Result, string FailureReason)
{
    public bool Succeeded => Result.Succeeded;
    public bool Superseded => Result.Superseded;
    public static SeekAttempt LandingFailed => new(EditorSeekResult.FailedResult, "landing timeout");
    public static SeekAttempt RollFailed => new(EditorSeekResult.FailedResult, "roll timeout");
    public static SeekAttempt SupersededResult => new(EditorSeekResult.SupersededResult, string.Empty);
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
