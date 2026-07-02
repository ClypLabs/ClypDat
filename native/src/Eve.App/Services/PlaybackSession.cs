using LibVLCSharp.Shared;

namespace Eve.App.Services;

public sealed class PlaybackSession : IDisposable
{
    private readonly LibVLC _libVlc;
    private Media? _videoMedia;
    private bool _disposed;

    public PlaybackSession()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--quiet");
        VideoPlayer = new MediaPlayer(_libVlc);
        VideoPlayer.EnableKeyInput = false;
        VideoPlayer.EnableMouseInput = false;
    }

    public MediaPlayer VideoPlayer { get; }
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, VideoPlayer.Length));
    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, VideoPlayer.Time));
    public bool IsPlaying => VideoPlayer.IsPlaying;

    public void Load(string path, IEnumerable<int> audioStreamIndexes)
    {
        Stop();
        DisposeMedia();
        _videoMedia = new Media(_libVlc, new Uri(path));
        VideoPlayer.Media = _videoMedia;
    }

    public void Play()
    {
        VideoPlayer.Play();
    }

    public void Pause()
    {
        VideoPlayer.Pause();
    }

    public void Stop()
    {
        VideoPlayer.Stop();
    }

    public void Seek(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        VideoPlayer.Time = milliseconds;
    }

    public void SetTrackVolume(int streamIndex, double percent)
    {
        var volume = (int)Math.Clamp(Math.Round(percent), 0, 150);
        VideoPlayer.Volume = volume;
    }

    public void SyncAudioStreams()
    {
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        VideoPlayer.Dispose();
        DisposeMedia();
        _libVlc.Dispose();
    }

    private void DisposeMedia()
    {
        _videoMedia?.Dispose();
        _videoMedia = null;
    }
}
