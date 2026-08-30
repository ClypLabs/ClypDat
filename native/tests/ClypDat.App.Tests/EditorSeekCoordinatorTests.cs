using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorSeekCoordinatorTests
{
    [Fact]
    public async Task Resume_RollRecovery_StartsAudioOnce()
    {
        var transport = new RecoveryTransport();
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            attemptTimeout: TimeSpan.FromMilliseconds(12));

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
    public async Task Resume_RollTimeout_NeverStartsAudio()
    {
        var transport = new RecoveryTransport(rollAfterRecovery: false);
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            attemptTimeout: TimeSpan.FromMilliseconds(12));

        var result = await coordinator.SeekAsync(
            transport,
            TimeSpan.FromSeconds(10),
            resume: true,
            seekId: "test",
            isCurrent: () => true,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, transport.AudioStarts);
    }

    [Fact]
    public async Task Resume_DeferredAudioAfterRecovery_StartsOnce()
    {
        var preparation = new TaskCompletionSource<AudioPreparationResult>();
        var transport = new RecoveryTransport(preparation.Task);
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            attemptTimeout: TimeSpan.FromMilliseconds(12));

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

    private sealed class RecoveryTransport : IEditorSeekTransport
    {
        private readonly bool _rollAfterRecovery;
        private readonly Task<AudioPreparationResult> _preparation;
        private TimeSpan _position;
        private int _recoveryCount;

        public RecoveryTransport(bool rollAfterRecovery = true)
            : this(Task.FromResult(new AudioPreparationResult(1, 0, false)), rollAfterRecovery) { }

        public RecoveryTransport(Task<AudioPreparationResult> preparation, bool rollAfterRecovery = true)
        {
            _preparation = preparation;
            _rollAfterRecovery = rollAfterRecovery;
        }

        public bool IsPaused { get; private set; }
        public TimeSpan Position => _position;
        public int AudioTrackCount => 1;
        public double PlaybackRate => 1;
        public string VideoState => IsPaused ? "Paused" : "Playing";
        public bool IsNetworkSource => false;
        public int AudioStarts { get; private set; }

        public Task<AudioPreparationResult> PrepareAudioAsync(TimeSpan target, string seekId) => _preparation;

        public void StopAudio() { }
        public void PauseVideo() => IsPaused = true;
        public void ResetVideo()
        {
            _recoveryCount++;
            IsPaused = true;
        }

        public void WritePosition(TimeSpan target) => _position = target;
        public void CommitPaused(TimeSpan position) => IsPaused = true;
        public void StartAudio(TimeSpan position, string seekId)
        {
            AudioStarts++;
        }

        public void CommitVideoOnly()
        {
            IsPaused = false;
            if (_rollAfterRecovery && _recoveryCount > 0) _position += TimeSpan.FromMilliseconds(25);
        }

        public void LogDebug(string line) { }
        public void LogInfo(string line) { }
        public void LogError(string line) { }
    }
}
