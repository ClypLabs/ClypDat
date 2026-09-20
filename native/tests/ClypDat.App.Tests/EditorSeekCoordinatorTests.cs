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
    public async Task ExhaustedLandingRecovery_LeavesTransportPaused()
    {
        var transport = new RecoveryTransport(presents: false);
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            attemptTimeout: TimeSpan.FromMilliseconds(4));

        var result = await coordinator.SeekAsync(
            transport,
            TimeSpan.FromSeconds(10),
            resume: true,
            seekId: "test",
            isCurrent: () => true,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(transport.IsPaused);
        Assert.Equal(0, transport.AudioStarts);
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
        Assert.True(transport.Presented);
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
        private readonly bool _presents;
        private readonly Task<AudioPreparationResult> _preparation;
        private TimeSpan _position;
        public bool CanReusePresentedFrame(TimeSpan target) => false;
        public bool Presented { get; private set; }
        public Task<bool> PresentAsync(TimeSpan target, Func<bool> current, CancellationToken token)
        {
            Presented = _presents;
            return Task.FromResult(_presents && current());
        }
        public RecoveryTransport(bool videoRolls = true, bool presents = true)
            : this(Task.FromResult(new AudioPreparationResult(1, 0, false)), videoRolls, presents) { }

        public RecoveryTransport(Task<AudioPreparationResult> preparation, bool videoRolls = true, bool presents = true)
        {
            _preparation = preparation;
            _videoRolls = videoRolls;
            _presents = presents;
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
            Assert.True(Presented);
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
