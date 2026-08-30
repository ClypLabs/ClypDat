using System.Diagnostics;

namespace ClypDat.App.Services;

internal sealed class EditorSeekCoordinator
{
    internal static readonly TimeSpan PositionTolerance = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan AudioReadyBudget = TimeSpan.FromMilliseconds(75);
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _attemptTimeout;
    private readonly Func<double>? _rate;

    public EditorSeekCoordinator(TimeSpan? pollInterval = null, TimeSpan? attemptTimeout = null, Func<double>? rate = null)
    { _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(10); _attemptTimeout = attemptTimeout ?? TimeSpan.FromMilliseconds(500); _rate = rate; }

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
        var preparation = transport.PrepareAudioAsync(target, seekId);
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
                    await preparation.ConfigureAwait(false);
                    if (!isCurrent()) return Terminal("superseded", EditorSeekResult.SupersededResult);
                    transport.CommitPaused(landed.Value);
                    transport.LogDebug($"seek={seekId} commit: mode=paused, audioAnchor={landed.Value.TotalSeconds:0.###}s, video={transport.VideoState}, wasapi=stopped, bufferMs=120.");
                    return Terminal("complete", new(true, false, false, landed.Value, landed.Value));
                }
                // Do not make audio audible until VLC has proved it is moving.
                // A slow roll used to retry after audio had already started at
                // this target, replaying that first half-second on affected clips.
                transport.CommitVideoOnly();
                transport.LogDebug($"seek={seekId} video-release: video={transport.VideoState}.");
                if (await WaitUntilAsync(() => transport.Position - landed.Value >= TimeSpan.FromMilliseconds(20), isCurrent, cancellationToken).ConfigureAwait(false))
                {
                    var ready = await WaitPreparationAsync(preparation, isCurrent, cancellationToken).ConfigureAwait(false);
                    if (!isCurrent()) return Terminal("superseded", EditorSeekResult.SupersededResult);
                    var audioAnchor = default(TimeSpan);
                    if (ready.ReadyTracks > 0)
                    {
                        audioAnchor = transport.Position;
                        transport.StartAudio(audioAnchor, seekId);
                        transport.LogDebug($"seek={seekId} audio-start: mode=synchronous, anchor={audioAnchor.TotalSeconds:0.###}s, wasapi=playing, bufferMs=120.");
                    }
                    else if (ready.Pending)
                    {
                        _ = StartDeferredAsync(transport, preparation, target, seekId, isCurrent);
                        transport.LogDebug($"seek={seekId} audio-start: mode=deferred, anchor=none, wasapi=stopped, bufferMs=120.");
                    }
                    else transport.LogDebug($"seek={seekId} audio-start: mode=silent, anchor=none, wasapi=stopped, bufferMs=120.");
                    transport.LogDebug($"seek={seekId} video-roll: ms={clock.ElapsedMilliseconds}, position={transport.Position.TotalSeconds:0.###}s.");
                    return Terminal("complete", new(true, true, false, landed.Value, audioAnchor));
                }
                if (!isCurrent()) return Terminal("superseded", EditorSeekResult.SupersededResult);
                if (reset == 1) return Fail("roll timeout");
                recovery++; transport.LogInfo($"seek={seekId} recovery-reset: reason=roll timeout, attempt={recovery}, state={transport.VideoState}, position={transport.Position.TotalSeconds:0.###}s."); transport.StopAudio(); transport.ResetVideo();
                if (!await WaitUntilAsync(() => transport.IsPaused, isCurrent, cancellationToken).ConfigureAwait(false)) return Fail("recovery reset timeout");
            }
        }
        catch (OperationCanceledException) { transport.LogDebug($"seek={seekId} cancelled: totalMs={clock.ElapsedMilliseconds}, recovery={recovery}."); throw; }
        catch (Exception error) { transport.LogError($"seek={seekId} failed: exception={error.Message}, totalMs={clock.ElapsedMilliseconds}, recovery={recovery}."); return Fail("exception"); }
        return Fail("unknown");
    }

    private async Task<TimeSpan?> LandAsync(IEditorSeekTransport transport, TimeSpan target, string id, Func<bool> current, CancellationToken token)
    {
        for (var attempt = 1; attempt <= 2; attempt++) { if (!current()) return null; var clock = Stopwatch.StartNew(); transport.PauseVideo(); if (!await WaitUntilAsync(() => transport.IsPaused, current, token).ConfigureAwait(false)) continue; transport.WritePosition(target); if (!await WaitUntilAsync(() => Math.Abs((transport.Position-target).TotalMilliseconds)<=PositionTolerance.TotalMilliseconds, current, token).ConfigureAwait(false)) continue; var landed=transport.Position; transport.LogDebug($"seek={id} video-landed: attempt={attempt}, requested={target.TotalSeconds:0.###}s, observed={landed.TotalSeconds:0.###}s, landingMs={clock.ElapsedMilliseconds}, deltaMs={(landed-target).TotalMilliseconds:0}."); return landed; } return null;
    }
    private async Task<AudioPreparationResult> WaitPreparationAsync(Task<AudioPreparationResult> task, Func<bool> current, CancellationToken token) { var done=await Task.WhenAny(task, Task.Delay(AudioReadyBudget, token)).ConfigureAwait(false); token.ThrowIfCancellationRequested(); return !current() ? AudioPreparationResult.PendingResult : done == task ? await task.ConfigureAwait(false) : AudioPreparationResult.PendingResult; }
    private async Task StartDeferredAsync(IEditorSeekTransport transport, Task<AudioPreparationResult> task, TimeSpan target, string id, Func<bool> current) { try { var result=await task.ConfigureAwait(false); if (!current() || result.ReadyTracks==0) return; var anchor=transport.Position; transport.StartAudio(anchor, id); transport.LogDebug($"seek={id} deferred-audio-start: target={target.TotalSeconds:0.###}s, anchor={anchor.TotalSeconds:0.###}s, ready={result.ReadyTracks}, failed={result.FailedTracks}."); } catch (Exception error) { transport.LogError($"seek={id} deferred-audio failed: {error.Message}"); } }
    private async Task<bool> WaitUntilAsync(Func<bool> predicate, Func<bool> current, CancellationToken token) { var clock=Stopwatch.StartNew(); while(clock.Elapsed<AttemptTimeout()) { token.ThrowIfCancellationRequested(); if(!current()) return false; if(predicate()) return true; await Task.Delay(_pollInterval, token).ConfigureAwait(false); } return current() && predicate(); }
}

internal interface IEditorSeekTransport
{ bool IsPaused { get; } TimeSpan Position { get; } int AudioTrackCount { get; } double PlaybackRate { get; } string VideoState { get; } bool IsNetworkSource { get; } Task<AudioPreparationResult> PrepareAudioAsync(TimeSpan target, string seekId); void StopAudio(); void PauseVideo(); void ResetVideo(); void WritePosition(TimeSpan target); void CommitPaused(TimeSpan position); void CommitVideoOnly(); void StartAudio(TimeSpan position, string seekId); void LogDebug(string line); void LogInfo(string line); void LogError(string line); }
internal readonly record struct AudioPreparationResult(int ReadyTracks, int FailedTracks, bool Pending) { public static AudioPreparationResult PendingResult => new(0, 0, true); }
internal readonly record struct EditorSeekResult(bool Succeeded, bool Resumed, bool Superseded, TimeSpan Landed, TimeSpan AudioAnchor) { public static EditorSeekResult FailedResult => new(false,false,false,default,default); public static EditorSeekResult SupersededResult => new(false,false,true,default,default); }

internal sealed class EditorAvClockPolicy
{ private const double DriftThresholdMilliseconds=150; private int _direction; private bool _corrected; private long _generation; public void Begin(long generation) { _generation=generation; _direction=0; _corrected=false; } public bool TryGetCorrection(long generation, TimeSpan elapsed, TimeSpan audible, TimeSpan video, out TimeSpan correction) { correction=default; if(generation!=_generation||_corrected||elapsed<TimeSpan.FromMilliseconds(250)||elapsed>TimeSpan.FromSeconds(1.5)) return false; var drift=video-audible; if(Math.Abs(drift.TotalMilliseconds)<=DriftThresholdMilliseconds) { _direction=0; return false; } var direction=Math.Sign(drift.TotalMilliseconds); if(_direction!=direction) { _direction=direction; return false; } _corrected=true; correction=video<TimeSpan.Zero?TimeSpan.Zero:video; return true; } public static TimeSpan ToMediaTime(TimeSpan anchor,long anchorDevicePosition,long devicePosition,int bytesPerSecond) => bytesPerSecond<=0?anchor:anchor+TimeSpan.FromSeconds(Math.Max(0,devicePosition-anchorDevicePosition)/(double)bytesPerSecond); }
