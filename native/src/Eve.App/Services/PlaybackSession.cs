using LibVLCSharp.Shared;

namespace Eve.App.Services;

public sealed class PlaybackSession : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly List<AudioStreamPlayer> _audioPlayers = new();
    private Media? _videoMedia;
    private bool _disposed;

    public PlaybackSession()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--quiet");
        VideoPlayer = new MediaPlayer(_libVlc);
        VideoPlayer.EnableKeyInput = false;
        VideoPlayer.EnableMouseInput = false;
        VideoPlayer.Mute = true;
    }

    public MediaPlayer VideoPlayer { get; }
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, VideoPlayer.Length));
    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, VideoPlayer.Time));
    public bool IsPlaying => VideoPlayer.IsPlaying;

    public void Load(string path, IEnumerable<int> audioStreamIndexes)
    {
        Stop();
        DisposeMedia();
        foreach (var audioPlayer in _audioPlayers)
        {
            audioPlayer.Dispose();
        }

        _audioPlayers.Clear();
        _videoMedia = new Media(_libVlc, new Uri(path));
        VideoPlayer.Media = _videoMedia;

        foreach (var streamIndex in audioStreamIndexes)
        {
            var media = new Media(_libVlc, new Uri(path));
            var player = new MediaPlayer(media)
            {
                EnableKeyInput = false,
                EnableMouseInput = false
            };
            _audioPlayers.Add(new AudioStreamPlayer(streamIndex, media, player));
        }
    }

    public void Play()
    {
        VideoPlayer.Play();
        foreach (var audioPlayer in _audioPlayers)
        {
            audioPlayer.Player.Play();
        }
    }

    public void Pause()
    {
        VideoPlayer.Pause();
        foreach (var audioPlayer in _audioPlayers)
        {
            audioPlayer.Player.Pause();
        }
    }

    public void Stop()
    {
        VideoPlayer.Stop();
        foreach (var audioPlayer in _audioPlayers)
        {
            audioPlayer.Player.Stop();
        }
    }

    public void Seek(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        VideoPlayer.Time = milliseconds;
        foreach (var audioPlayer in _audioPlayers)
        {
            audioPlayer.Player.Time = milliseconds;
        }
    }

    public void SetTrackVolume(int streamIndex, double percent)
    {
        var volume = (int)Math.Clamp(Math.Round(percent), 0, 150);
        foreach (var audioPlayer in _audioPlayers.Where(player => player.StreamIndex == streamIndex))
        {
            audioPlayer.Player.Volume = volume;
        }
    }

    public void SyncAudioStreams()
    {
        foreach (var audioPlayer in _audioPlayers)
        {
            audioPlayer.Player.SetAudioTrack(audioPlayer.StreamIndex);
            audioPlayer.Player.Time = VideoPlayer.Time;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        foreach (var audioPlayer in _audioPlayers)
        {
            audioPlayer.Dispose();
        }

        _audioPlayers.Clear();
        VideoPlayer.Dispose();
        DisposeMedia();
        _libVlc.Dispose();
    }

    private void DisposeMedia()
    {
        _videoMedia?.Dispose();
        _videoMedia = null;
    }

    private sealed class AudioStreamPlayer : IDisposable
    {
        public AudioStreamPlayer(int streamIndex, Media media, MediaPlayer player)
        {
            StreamIndex = streamIndex;
            Media = media;
            Player = player;
        }

        public int StreamIndex { get; }
        public Media Media { get; }
        public MediaPlayer Player { get; }

        public void Dispose()
        {
            Player.Dispose();
            Media.Dispose();
        }
    }
}
