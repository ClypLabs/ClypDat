using System.Diagnostics;

namespace ClypDat.App.Services;

internal sealed class EditorSeekCoordinator
{
    internal static readonly TimeSpan PositionTolerance = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan AudioReadyBudget = TimeSpan.FromMilliseconds(75);
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _attemptTimeout;
    // How long an open holds the loading poster for its first audio chunks.
    // Local extraction measured 146-476ms for four tracks at once; past this
    // the clip plays and its sound joins when the chunks land. A share
    // serialises extraction (ChunkedAudioReader.NetworkExtractionGate), so it
    // gets longer. Unbounded, one hung ffmpeg held the poster for its whole
    // 10s/30s extraction timeout.
    private static readonly TimeSpan StartAudioBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NetworkStartAudioBudget = TimeSpan.FromSeconds(3);
    // Motion is detected on the first new picture after the landing frame, so
    // the video is already about a frame past it when audio is anchored.
    private static readonly TimeSpan FirstMotionLead = TimeSpan.FromMilliseconds(20);
    private readonly TimeSpan _rollTimeout;
    private readonly TimeSpan _startAudioBudget;
    private readonly TimeSpan _networkStartAudioBudget;
    private readonly Func<double>? _rate;

    public EditorSeekCoordinator(TimeSpan? pollInterval = null, TimeSpan? attemptTimeout = null, Func<double>? rate = null, TimeSpan? rollTimeout = null, TimeSpan? startAudioBudget = null)
    { _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(10); _attemptTimeout = attemptTimeout ?? TimeSpan.FromMilliseconds(500); _rate = rate; _rollTimeout = rollTimeout ?? TimeSpan.FromSeconds(1.5); _startAudioBudget = startAudioBudget ?? StartAudioBudget; _networkStartAudioBudget = startAudioBudget ?? NetworkStartAudioBudget; }

    private TimeSpan AttemptTimeout()
    { var rate = _rate?.Invoke() ?? 1; if (double.IsNaN(rate) || double.IsInfinity(rate)) rate = 1; return _attemptTimeout / Math.Clamp(rate, .25, 1); }

    public async Task<EditorSeekResult> SeekAsync(IEditorSeekTransport transport, TimeSpan target, bool resume, string seekId, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        target = target < TimeSpan.Zero ? TimeSpan.Zero : target;
        var clock = Stopwatch.StartNew(); var recovery = 0;
        EditorSeekResult Terminal(string state, EditorSeekResult result) { transport.LogDebug($"seek={seekId} {state}: totalMs={clock.ElapsedMilliseconds}, landed={result.Landed.TotalSeconds:0.###}s, resumed={result.Resumed}, recovery={recovery}."); return result; }
        EditorSeekResult Fail(string reason) { if (isCurrent()) { transport.StopAudio(); transport.PauseVideo(); } transport.LogError($"seek={seekId} failed: reason={reason}, totalMs={clock.ElapsedMilliseconds}, recovery={recovery}."); return EditorSeekResult.FailedResult; }
        if (!isCurrent()) return Terminal("superseded", EditorSeekResult.SupersededResult);
        transport.LogDebug($"seek={seekId} request: target={target.TotalSeconds:0.###}s, resume={resume}, rate={transport.PlaybackRate:0.###}x, video={transport.VideoState}, tracks={transport.AudioTrackCount}, network={transport.IsNetworkSource}.");
        transport.StopAudio();
        var preparation = transport.PrepareAudioAsync(target, seekId, cancellationToken);
        try
        {
            for (var reset = 0; reset < 2; reset++)
            {
                var landed = await LandAsync(transport, target, seekId, isCurrent, cancellationToken).ConfigureAwait(false);
                if (landed is null)
                {
                    if (!isCurrent()) return Terminal("superseded", EditorSeekResult.SupersededResult);
                    if (reset == 1) return Fail("landing timeout");
                    recovery++; transport.LogInfo($"seek={seekId} recovery-reset: reason=landing timeout, attempt={recovery}, state={transport.VideoState}, position={transport.Position.TotalSeconds:0.###}s.");
                    transport.StopAudio(); transport.ResetVideo();
                    if (!await WaitUntilAsync(() => transport.IsPaused, isCurrent, cancellationToken).ConfigureAwait(false)) return Fail("recovery reset timeout");
                    continue;
                }
                if (!resume)
                {
                    await preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (!isCurrent()) return Terminal("superseded", EditorSeekResult.SupersededResult);
                    transport.CommitPaused(landed.Value);
                    transport.LogDebug($"seek={seekId} commit: mode=paused, audioAnchor={landed.Value.TotalSeconds:0.###}s, video={transport.VideoState}, wasapi=stopped, bufferMs=120.");
                    return Terminal("complete", new(true, false, false, landed.Value, landed.Value));
                }
                var ready = await WaitPreparationAsync(preparation, isCurrent, cancellationToken).ConfigureAwait(false);
                if (!isCurrent()) return Terminal("superseded", EditorSeekResult.SupersededResult);
                if (ready.ReadyTracks > 0)
                {
                    transport.CommitPlaying(landed.Value, seekId);
                    transport.LogDebug($"seek={seekId} commit: mode=synchronous, audioAnchor={landed.Value.TotalSeconds:0.###}s, video={transport.VideoState}, wasapi=playing, bufferMs=120.");
                }
                else
                {
                    transport.CommitVideoOnly();
                    transport.LogDebug($"seek={seekId} commit: mode={(ready.Pending ? "deferred" : "silent")}, audioAnchor=none, video={transport.VideoState}, wasapi=stopped, bufferMs=120.");
                    if (ready.Pending) _ = StartDeferredAsync(transport, preparation, target, seekId, isCurrent);
                }
                // Audio is already audible. Do not reset and replay it if VLC
                // rolls slowly; wait for the slow clips seen in production,
                // then fail cleanly rather than committing audio a second time.
                if (await WaitUntilAsync(() => transport.Position - landed.Value >= TimeSpan.FromMilliseconds(20), isCurrent, cancellationToken, _rollTimeout).ConfigureAwait(false))
                {
                    transport.LogDebug($"seek={seekId} video-roll: ms={clock.ElapsedMilliseconds}, position={transport.Position.TotalSeconds:0.###}s.");
                    return Terminal("complete", new(true, true, false, landed.Value, ready.ReadyTracks > 0 ? landed.Value : default));
                }
                if (!isCurrent()) return Terminal("superseded", EditorSeekResult.SupersededResult);
                return Fail("roll timeout");
            }
        }
        catch (OperationCanceledException) { transport.LogDebug($"seek={seekId} cancelled: totalMs={clock.ElapsedMilliseconds}, recovery={recovery}."); throw; }
        catch (Exception error) { transport.LogError($"seek={seekId} failed: exception={error.Message}, totalMs={clock.ElapsedMilliseconds}, recovery={recovery}."); return Fail("exception"); }
        return Fail("unknown");
    }

    // Opening order for a clip coming in from the Library. The view stays
    // parked behind the loading poster while the first frame lands and the
    // audio gets a bounded wait; then the view is revealed, the video started,
    // and only once a NEW picture has actually been presented does the audio
    // start - anchored to where the video now is. Audio used to start in the
    // same breath as SetPause(false), which is a request VLC honours 160-480ms
    // later, so the clip's sound ran ahead of a picture that had not moved.
    //
    // Audio that misses the budget joins late through StartDeferredAsync. That
    // never starts WASAPI before its chunk exists, so there is no silent gap -
    // the reader only plays silence when it is started on a missing chunk.
    public async Task<EditorPlaybackStartResult> StartAsync(IEditorSeekTransport transport, TimeSpan target, string startId, Func<bool> isCurrent, CancellationToken cancellationToken, TimeSpan? startupTimeout = null)
    {
        target = target < TimeSpan.Zero ? TimeSpan.Zero : target;
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = startupTimeout ?? TimeSpan.FromSeconds(15);
        deadline.CancelAfter(limit > TimeSpan.Zero ? limit : TimeSpan.Zero);
        var token = deadline.Token;
        var failure = EditorPlaybackStartFailure.PauseTimeout;
        EditorPlaybackStartResult Fail(EditorPlaybackStartFailure reason)
        {
            if (isCurrent()) { transport.StopAudio(); transport.PauseVideo(); }
            transport.LogError($"start={startId} failed: stage={reason}, ms={clock.ElapsedMilliseconds}, network={transport.IsNetworkSource}, state={transport.VideoState}, position={transport.Position.TotalSeconds:0.###}s, presented={transport.PresentedPicture}.");
            return EditorPlaybackStartResult.FailedResult with { Failure = reason };
        }
        EditorPlaybackStartResult Superseded() { transport.LogDebug($"start={startId} superseded: ms={clock.ElapsedMilliseconds}."); return EditorPlaybackStartResult.SupersededResult; }
        if (!isCurrent()) return Superseded();
        if (limit <= TimeSpan.Zero) return Fail(EditorPlaybackStartFailure.DeadlineExceeded);
        transport.LogDebug($"start={startId} prepare: target={target.TotalSeconds:0.###}s, tracks={transport.AudioTrackCount}.");
        var preparation = transport.PrepareAudioAsync(target, startId, cancellationToken);
        try
        {
            var landed = transport.CanReusePresentedFrame(target) ? target : await LandAsync(transport, target, startId, isCurrent, token,
                transport.IsNetworkSource ? () => limit - clock.Elapsed : null, reason => failure = reason).ConfigureAwait(false);
            if (landed is null) return !isCurrent() ? Superseded() : Fail(clock.Elapsed >= limit ? EditorPlaybackStartFailure.DeadlineExceeded : failure);

            // Polled rather than awaited so a seek or a newer open supersedes
            // this within one interval instead of holding the seek lock for a
            // whole extraction.
            var budget = (transport.IsNetworkSource ? _networkStartAudioBudget : _startAudioBudget) - clock.Elapsed;
            await WaitUntilAsync(() => preparation.IsCompleted, isCurrent, token, budget > TimeSpan.Zero ? budget : TimeSpan.Zero).ConfigureAwait(false);
            if (!isCurrent()) return Superseded();
            var audio = AudioOutcome(transport, preparation, startId);

            if (!await transport.RevealAsync(token).ConfigureAwait(false) || !isCurrent()) return Superseded();
            // Read after the reveal: re-showing the parked picture in its new
            // place is a redraw, not a new picture, but a straggling landing
            // decode must not count as the video moving.
            // The position fallback is measured from here too, not from the
            // landing target: a reused warm frame may sit up to 150ms off it.
            var baseline = transport.PresentedPicture;
            var basePosition = transport.Position;
            transport.PlayVideo();
            var moved = await WaitUntilAsync(() => transport.PresentedPicture > baseline || transport.Position - basePosition >= TimeSpan.FromMilliseconds(20), isCurrent, token, _rollTimeout).ConfigureAwait(false);
            if (!isCurrent()) return Superseded();
            if (!moved)
            {
                transport.PauseVideo();
                transport.LogInfo($"start={startId} revealed-paused: video did not roll within {_rollTimeout.TotalMilliseconds:0}ms, position={transport.Position.TotalSeconds:0.###}s, ms={clock.ElapsedMilliseconds}.");
                return new(EditorPlaybackStartOutcome.RevealedPaused, landed.Value, landed.Value, audio);
            }

            var moving = transport.Position > basePosition ? transport.Position : basePosition;
            var anchor = moving > landed.Value + FirstMotionLead ? moving : landed.Value + FirstMotionLead;
            transport.ResumeClock(anchor);
            if (audio.ReadyTracks > 0) transport.StartDeferredAudio(anchor, startId);
            else if (audio.Pending) _ = StartDeferredAsync(transport, preparation, target, startId, () => !cancellationToken.IsCancellationRequested && isCurrent());
            transport.LogDebug($"start={startId} commit: landed={landed.Value.TotalSeconds:0.###}s, anchor={anchor.TotalSeconds:0.###}s, audio={(audio.ReadyTracks > 0 ? "after-motion" : audio.Pending ? "deferred" : "silent")}, ready={audio.ReadyTracks}, failed={audio.FailedTracks}, ms={clock.ElapsedMilliseconds}.");
            return new(audio.Pending ? EditorPlaybackStartOutcome.AudioPending : EditorPlaybackStartOutcome.Playing, landed.Value, anchor, audio);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return !isCurrent() ? Superseded() : Fail(EditorPlaybackStartFailure.DeadlineExceeded);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            transport.LogError($"start={startId} failed: {error.Message}");
            return Fail(EditorPlaybackStartFailure.PlayerError);
        }
    }

    // A preparation that faulted (the audio load threw) plays the clip silent
    // rather than failing the open; the audio load reports its own error.
    private static AudioPreparationResult AudioOutcome(IEditorSeekTransport transport, Task<AudioPreparationResult> preparation, string startId)
    {
        if (!preparation.IsCompleted) return AudioPreparationResult.PendingResult;
        if (preparation.IsCompletedSuccessfully) return preparation.Result;
        if (preparation.IsCanceled) throw new OperationCanceledException();
        transport.LogError($"start={startId} audio-prepare failed: {preparation.Exception?.GetBaseException().Message}");
        return new AudioPreparationResult(0, transport.AudioTrackCount, false);
    }

    private async Task<TimeSpan?> LandAsync(IEditorSeekTransport transport, TimeSpan target, string id, Func<bool> current, CancellationToken token,
        Func<TimeSpan>? remainingStartup = null, Action<EditorPlaybackStartFailure>? failed = null)
    {
        // The player parks on its landing frame after a scrub preview, so a
        // settling seek to that same frame has nothing to decode. Writing the
        // position anyway raises a compositor seek barrier the decoder will
        // never satisfy, and the presentation wait then burns its whole budget
        // on a frame that is already on screen.
        if (transport.IsParkedOnFrame(target))
        {
            // PresentAsync is what normally freezes the clock on the landing
            // frame; hold that invariant for the frame that is already up.
            transport.FreezeOnFrame(target);
            transport.LogDebug($"seek={id} landing: reused parked frame at {target.TotalSeconds:0.###}s.");
            return target;
        }
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (!current()) return null;
            var clock = Stopwatch.StartNew();
            transport.PauseVideo();
            // Pause requests issued while VLC opens/buffers can be ignored.
            // Reassert only once playable; never restart an input still opening.
            var paused = await WaitUntilAsync(() =>
            {
                if (transport.IsPaused || transport.VideoState == "Error") return true;
                if (transport.VideoState == "Playing") transport.PauseVideo();
                return transport.IsPaused;
            }, current, token, remainingStartup?.Invoke()).ConfigureAwait(false);
            if (!current()) return null;
            if (transport.VideoState == "Error") { failed?.Invoke(EditorPlaybackStartFailure.PlayerError); return null; }
            if (!paused || !transport.IsPaused) { failed?.Invoke(EditorPlaybackStartFailure.PauseTimeout); continue; }
            transport.WritePosition(target);
            if (!await transport.PresentAsync(target, current, token).ConfigureAwait(false))
            { failed?.Invoke(EditorPlaybackStartFailure.PresentationTimeout); continue; }
            if (!current()) return null;
            transport.LogDebug($"seek={id} video-presented: attempt={attempt}, requested={target.TotalSeconds:0.###}s, presentationMs={clock.ElapsedMilliseconds}.");
            return target;
        }
        return null;
    }
    private async Task<AudioPreparationResult> WaitPreparationAsync(Task<AudioPreparationResult> task, Func<bool> current, CancellationToken token) { var done=await Task.WhenAny(task, Task.Delay(AudioReadyBudget, token)).ConfigureAwait(false); token.ThrowIfCancellationRequested(); return !current() ? AudioPreparationResult.PendingResult : done == task ? await task.ConfigureAwait(false) : AudioPreparationResult.PendingResult; }
    private async Task StartDeferredAsync(IEditorSeekTransport transport, Task<AudioPreparationResult> task, TimeSpan target, string id, Func<bool> current) { try { var result=await task.ConfigureAwait(false); if (!current() || result.ReadyTracks==0) return; var anchor=transport.LivePosition; transport.StartDeferredAudio(anchor, id); transport.LogDebug($"seek={id} deferred-audio-start: target={target.TotalSeconds:0.###}s, anchor={anchor.TotalSeconds:0.###}s, ready={result.ReadyTracks}, failed={result.FailedTracks}."); } catch (Exception error) { transport.LogError($"seek={id} deferred-audio failed: {error.Message}"); } }
    private async Task<bool> WaitUntilAsync(Func<bool> predicate, Func<bool> current, CancellationToken token, TimeSpan? timeout = null) { var clock=Stopwatch.StartNew(); var limit=timeout ?? AttemptTimeout(); while(clock.Elapsed<limit) { token.ThrowIfCancellationRequested(); if(!current()) return false; if(predicate()) return true; await Task.Delay(_pollInterval, token).ConfigureAwait(false); } return current() && predicate(); }
}

internal interface IEditorSeekTransport
{ bool CanReusePresentedFrame(TimeSpan target); bool IsParkedOnFrame(TimeSpan target); void FreezeOnFrame(TimeSpan target); Task<bool> PresentAsync(TimeSpan target, Func<bool> current, CancellationToken token); bool IsPaused { get; } TimeSpan Position { get; }
  // Position interpolated between VLC's coarse time updates - what a late
  // audio join anchors to, so it lands where the picture is now.
  TimeSpan LivePosition { get; }
  // Advances once per NEW picture presented (redraws of a parked one do not).
  ulong PresentedPicture { get; }
  // Opening only: take the view off its loading poster. False when the open
  // it belongs to is no longer the current one.
  Task<bool> RevealAsync(CancellationToken token); void PlayVideo(); void ResumeClock(TimeSpan anchor);
  int AudioTrackCount { get; } double PlaybackRate { get; } string VideoState { get; } bool IsNetworkSource { get; } Task<AudioPreparationResult> PrepareAudioAsync(TimeSpan target, string seekId, CancellationToken cancellationToken = default); void StopAudio(); void PauseVideo(); void ResetVideo(); void WritePosition(TimeSpan target); void CommitPaused(TimeSpan position); void CommitPlaying(TimeSpan position, string seekId); void CommitVideoOnly(); void StartDeferredAudio(TimeSpan position, string seekId); void LogDebug(string line); void LogInfo(string line); void LogError(string line); }
internal readonly record struct AudioPreparationResult(int ReadyTracks, int FailedTracks, bool Pending) { public static AudioPreparationResult PendingResult => new(0, 0, true); }
internal readonly record struct EditorSeekResult(bool Succeeded, bool Resumed, bool Superseded, TimeSpan Landed, TimeSpan AudioAnchor) { public static EditorSeekResult FailedResult => new(false,false,false,default,default); public static EditorSeekResult SupersededResult => new(false,false,true,default,default); }
internal enum EditorPlaybackStartFailure { None, PauseTimeout, PresentationTimeout, DeadlineExceeded, PlayerError }
internal enum EditorPlaybackStartOutcome { Playing, AudioPending, RevealedPaused, Failed, Superseded }
internal readonly record struct EditorPlaybackStartResult(EditorPlaybackStartOutcome Outcome, TimeSpan Landed, TimeSpan Anchor, AudioPreparationResult Audio, EditorPlaybackStartFailure Failure = EditorPlaybackStartFailure.None)
{
    public bool Succeeded => Outcome is EditorPlaybackStartOutcome.Playing or EditorPlaybackStartOutcome.AudioPending;
    public bool Superseded => Outcome == EditorPlaybackStartOutcome.Superseded;
    public static EditorPlaybackStartResult FailedResult => new(EditorPlaybackStartOutcome.Failed, default, default, default);
    public static EditorPlaybackStartResult SupersededResult => new(EditorPlaybackStartOutcome.Superseded, default, default, default);
}

internal sealed class EditorAvClockPolicy
{ private const double DriftThresholdMilliseconds=150; private int _direction; private bool _corrected; private long _generation; public void Begin(long generation) { _generation=generation; _direction=0; _corrected=false; } public bool TryGetCorrection(long generation, TimeSpan elapsed, TimeSpan audible, TimeSpan video, out TimeSpan correction) { correction=default; if(generation!=_generation||_corrected||elapsed<TimeSpan.FromMilliseconds(250)||elapsed>TimeSpan.FromSeconds(1.5)) return false; var drift=video-audible; if(Math.Abs(drift.TotalMilliseconds)<=DriftThresholdMilliseconds) { _direction=0; return false; } var direction=Math.Sign(drift.TotalMilliseconds); if(_direction!=direction) { _direction=direction; return false; } _corrected=true; correction=video<TimeSpan.Zero?TimeSpan.Zero:video; return true; } public static TimeSpan ToMediaTime(TimeSpan anchor,long anchorDevicePosition,long devicePosition,int bytesPerSecond) => bytesPerSecond<=0?anchor:anchor+TimeSpan.FromSeconds(Math.Max(0,devicePosition-anchorDevicePosition)/(double)bytesPerSecond); }
