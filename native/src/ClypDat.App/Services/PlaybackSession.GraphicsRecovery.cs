using LibVLCSharp.Shared;

namespace ClypDat.App.Services;

internal sealed class EditorGraphicsRestartRequiredException() : InvalidOperationException(
    "Graphics teardown could not finish safely. Your edits are retained. Restart ClypDat to restore video preview.");

public sealed partial class PlaybackSession
{
    private readonly List<string> _videoOptions = [];
    private volatile bool _graphicsRecoveryActive, _graphicsRestartRequired;
    internal bool GraphicsRestartRequired => _graphicsRestartRequired;
    private void AddVideoOption(string option) { _videoOptions.Add(option); _videoMedia!.AddOption(option); }
    internal void BeginGraphicsRecovery(TimeSpan position)
    {
        _lastRequestedPosition = position; _graphicsRecoveryActive = true;
        Interlocked.Increment(ref _seekVersion); Interlocked.Increment(ref _playVersion);
        _previewRequests.BeginFinalSeek();
    }
    internal void EndGraphicsRecovery() => _graphicsRecoveryActive = false;
    internal Task<PlaybackSeekResult> PresentGraphicsRecoveryAsync(TimeSpan position, CancellationToken token)
        => SeekCoreAsync(position, false, token);
    internal Task RestoreGraphicsRateAsync(CancellationToken token) => Task.Run(() => {
        lock (_transportLock) { VideoPlayer.SetRate((float)_playbackRate); _rateStage?.SetRate(_playbackRate); }
    }, token);

    // Stop owns VLC's output/decoder threads. A timed-out stop must retain all
    // referenced native resources; freeing them from the UI would be unsafe.
    internal async Task RebuildGraphicsOutputAsync(string path, CancellationToken token)
    {
        using var load = await _loadGate.EnterAsync(token).ConfigureAwait(false);
        if (_graphicsRestartRequired) throw new EditorGraphicsRestartRequiredException();
        if (_disposed || !string.Equals(LoadedPath, path, StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException(token);
        _graphicsRecoveryActive = true;
        var acquired = false;
        try
        {
            await _seekLock.WaitAsync(token).WaitAsync(GraphicsRecovery.ControlTimeout, token).ConfigureAwait(false); acquired = true;
            var teardown = Task.Run(() => {
                lock (_transportLock) {
                    StopAudioClockMonitoring(); _audioOutput?.Stop(); VideoPlayer.Stop();
                    DisposeMedia();
                }
            });
            // Cancellation does not release the load gate while native stop is
            // still running. Navigation can reuse it only after confirmed stop.
            await teardown.WaitAsync(GraphicsRecovery.ControlTimeout).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (_disposed || !string.Equals(LoadedPath, path, StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException(token);
            await Task.Run(() => {
                _videoMedia = new Media(_libVlc, new Uri(path));
                Composition = new NativeVideoOutput(); Composition.BindPlayer(VideoPlayer);
                _videoMedia.AddOption(Composition.MediaOption);
                foreach (var option in _videoOptions.Where(option => !option.StartsWith(":clypdat-context=", StringComparison.Ordinal))) _videoMedia.AddOption(option);
                VideoPlayer.Media = _videoMedia; ForceVideoSilent();
                VideoPlayer.SetRate((float)_playbackRate);
                _ended = false; ResetOverlayClock(_lastRequestedPosition);
                ReapplyCropMaskImage();
            }, token).ConfigureAwait(false);
        }
        catch (TimeoutException) { _graphicsRestartRequired = true; throw new EditorGraphicsRestartRequiredException(); }
        finally { if (acquired) _seekLock.Release(); }
    }
}
