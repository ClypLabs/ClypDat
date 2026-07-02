using LibVLCSharp.Shared;

namespace Eve.App.Services;

public sealed class PlaybackSession : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly List<AudioTrackPlayer> _audioPlayers = new();
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
        DisposeAudioPlayers();
        _videoMedia = new Media(_libVlc, new Uri(path));
        VideoPlayer.Media = _videoMedia;
        VideoPlayer.Mute = true;

        var audioStreams = audioStreamIndexes.ToArray();
        for (var i = 0; i < audioStreams.Length; i++)
        {
            var media = new Media(_libVlc, new Uri(path));
            media.AddOption(":no-video");
            media.AddOption($":audio-track={i}");
            media.AddOption($":audio-track-id={audioStreams[i]}");
            var player = new MediaPlayer(media)
            {
                EnableKeyInput = false,
                EnableMouseInput = false,
                Mute = false,
                Volume = 100
            };
            _audioPlayers.Add(new AudioTrackPlayer(audioStreams[i], i, media, player));
        }
    }

    public void Play()
    {
        VideoPlayer.Play();
        foreach (var audio in _audioPlayers)
        {
            audio.Player.Play();
            ApplyAudioTrack(audio);
            audio.Player.Time = VideoPlayer.Time;
            ScheduleAudioTrackApply(audio, VideoPlayer.Time);
        }
    }

    public void Pause()
    {
        VideoPlayer.Pause();
        foreach (var audio in _audioPlayers)
        {
            audio.Player.Pause();
        }
    }

    public void Stop()
    {
        VideoPlayer.Stop();
        foreach (var audio in _audioPlayers)
        {
            audio.Player.Stop();
        }
    }

    public void Seek(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        VideoPlayer.Time = milliseconds;
        foreach (var audio in _audioPlayers)
        {
            audio.Player.Time = milliseconds;
        }
    }

    public void SetTrackVolume(int streamIndex, double percent)
    {
        var volume = VolumeCurve(percent);
        foreach (var audio in _audioPlayers.Where(audio => audio.StreamIndex == streamIndex))
        {
            audio.Player.Volume = volume;
        }
    }

    public void SyncAudioStreams()
    {
        var videoTime = VideoPlayer.Time;
        foreach (var audio in _audioPlayers)
        {
            ApplyAudioTrack(audio);
            if (Math.Abs(audio.Player.Time - videoTime) > 150)
            {
                audio.Player.Time = videoTime;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        VideoPlayer.Dispose();
        DisposeAudioPlayers();
        DisposeMedia();
        _libVlc.Dispose();
    }

    private void DisposeMedia()
    {
        _videoMedia?.Dispose();
        _videoMedia = null;
    }

    private void DisposeAudioPlayers()
    {
        foreach (var audio in _audioPlayers)
        {
            audio.Player.Dispose();
            audio.Media.Dispose();
        }

        _audioPlayers.Clear();
    }

    private static int VolumeCurve(double percent)
    {
        var clamped = Math.Clamp(percent, 0, 150);
        var volume = clamped <= 100
            ? Math.Sqrt(clamped / 100) * 100
            : clamped;
        return (int)Math.Clamp(Math.Round(volume), 0, 150);
    }

    private static void ApplyAudioTrack(AudioTrackPlayer audio)
    {
        var tracks = audio.Player.AudioTrackDescription
            .Where(description => description.Id >= 0)
            .ToArray();
        if (audio.AudioOrdinal >= tracks.Length) return;

        var track = tracks[audio.AudioOrdinal];
        if (audio.Player.AudioTrack == track.Id) return;
        audio.Player.SetAudioTrack(track.Id);
    }

    private static void ScheduleAudioTrackApply(AudioTrackPlayer audio, long syncTime)
    {
        _ = Task.Run(async () =>
        {
            foreach (var delay in new[] { 75, 200, 500 })
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (audio.Player.Media is null) return;
                ApplyAudioTrack(audio);
                audio.Player.Time = syncTime;
            }
        });
    }

    private sealed record AudioTrackPlayer(int StreamIndex, int AudioOrdinal, Media Media, MediaPlayer Player);
}
