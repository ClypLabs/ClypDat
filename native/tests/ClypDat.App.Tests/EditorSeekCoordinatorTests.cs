using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorSeekCoordinatorTests
{
    [Fact]
    public async Task Resume_WhenVideoRolls_StartsAudioImmediately()
    {
        var transport = new RecoveryTransport();
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            attemptTimeout: TimeSpan.FromMilliseconds(12),
            rollTimeout: TimeSpan.FromMilliseconds(12));

        var result = await coordinator.SeekAsync(
            transport,
            TimeSpan.FromSeconds(10),
            resume: true,
            seekId: "test",
            isCurrent: () => true,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Resumed);
        Assert.Equal(1, transport.AudioStarts);
    }

    [Fact]
    public async Task Resume_RollTimeout_DoesNotReplayAudio()
    {
        var transport = new RecoveryTransport(videoRolls: false);
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            attemptTimeout: TimeSpan.FromMilliseconds(12),
            rollTimeout: TimeSpan.FromMilliseconds(12));

        var result = await coordinator.SeekAsync(
            transport,
            TimeSpan.FromSeconds(10),
            resume: true,
            seekId: "test",
            isCurrent: () => true,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, transport.AudioStarts);
    }

    [Fact]
    public async Task Resume_DeferredAudioAfterVideoRoll_StartsOnce()
    {
        var preparation = new TaskCompletionSource<AudioPreparationResult>();
        var transport = new RecoveryTransport(preparation.Task);
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            attemptTimeout: TimeSpan.FromMilliseconds(12),
            rollTimeout: TimeSpan.FromMilliseconds(12));

        var result = await coordinator.SeekAsync(
            transport,
            TimeSpan.FromSeconds(10),
            resume: true,
            seekId: "test",
            isCurrent: () => true,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, transport.AudioStarts);
        preparation.SetResult(new AudioPreparationResult(1, 0, false));
        Assert.True(SpinWait.SpinUntil(() => transport.AudioStarts == 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Startup_DelayedAudio_DoesNotCommitVideoEarly()
    {
        var preparation = new TaskCompletionSource<AudioPreparationResult>();
        var transport = new RecoveryTransport(preparation.Task);
        var coordinator = new EditorSeekCoordinator(pollInterval: TimeSpan.FromMilliseconds(1));

        var startup = coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => true, CancellationToken.None);

        await Task.Delay(20);
        Assert.Equal(0, transport.AudioStarts);
        Assert.True(transport.IsPaused);
        preparation.SetResult(new AudioPreparationResult(1, 0, false));
        var result = await startup;
        Assert.True(result.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Landed);
        Assert.Equal(1, transport.AudioStarts);
        Assert.False(transport.IsPaused);
    }

    [Fact]
    public async Task Startup_SilentSource_StartsVideoWithoutAudioWait()
    {
        var transport = new RecoveryTransport(Task.FromResult(new AudioPreparationResult(0, 0, false)));
        var coordinator = new EditorSeekCoordinator(pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, transport.AudioStarts);
        Assert.False(transport.IsPaused);
    }

    private sealed class RecoveryTransport : IEditorSeekTransport
    {
        private readonly bool _videoRolls;
        private readonly Task<AudioPreparationResult> _preparation;
        private TimeSpan _position;
        public RecoveryTransport(bool videoRolls = true)
            : this(Task.FromResult(new AudioPreparationResult(1, 0, false)), videoRolls) { }

        public RecoveryTransport(Task<AudioPreparationResult> preparation, bool videoRolls = true)
        {
            _preparation = preparation;
            _videoRolls = videoRolls;
        }

        public bool IsPaused { get; private set; }
        public TimeSpan Position => _position;
        public int AudioTrackCount => 1;
        public double PlaybackRate => 1;
        public string VideoState => IsPaused ? "Paused" : "Playing";
        public bool IsNetworkSource => false;
        public int AudioStarts { get; private set; }

        public Task<AudioPreparationResult> PrepareAudioAsync(TimeSpan target, string seekId, CancellationToken cancellationToken = default) => _preparation;

        public void StopAudio() { }
        public void PauseVideo() => IsPaused = true;
        public void ResetVideo() => IsPaused = true;

        public void WritePosition(TimeSpan target) => _position = target;
        public void CommitPaused(TimeSpan position) => IsPaused = true;
        public void CommitPlaying(TimeSpan position, string seekId)
        {
            AudioStarts++;
            IsPaused = false;
            if (_videoRolls) _position += TimeSpan.FromMilliseconds(25);
        }

        public void CommitVideoOnly()
        {
            IsPaused = false;
            if (_videoRolls) _position += TimeSpan.FromMilliseconds(25);
        }

        public void StartDeferredAudio(TimeSpan position, string seekId) => AudioStarts++;

        public void LogDebug(string line) { }
        public void LogInfo(string line) { }
        public void LogError(string line) { }
    }
}
