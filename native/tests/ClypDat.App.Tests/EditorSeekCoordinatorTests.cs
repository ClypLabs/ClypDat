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
    public async Task ParkedOnTargetFrame_LandsWithoutRaisingAnotherSeekBarrier()
    {
        var transport = new RecoveryTransport(presents: false) { Parked = true, Start = TimeSpan.FromSeconds(12) };

        var result = await new EditorSeekCoordinator().SeekAsync(
            transport, TimeSpan.FromSeconds(12), resume: true, "parked", () => true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Resumed);
        Assert.Equal(0, transport.Writes);
        Assert.Equal(1, transport.Freezes);
        Assert.False(transport.IsPaused);
        Assert.Equal(1, transport.AudioStarts);
    }

    private sealed class RecoveryTransport : IEditorSeekTransport
    {
        private readonly bool _videoRolls;
        private readonly bool _presents;
        private readonly Task<AudioPreparationResult> _preparation;
        private TimeSpan _position;
        public bool CanReusePresentedFrame(TimeSpan target) => false;
        // The player is already parked on the requested frame, so the seek has
        // nothing to write or decode.
        public bool Parked { get; init; }
        public TimeSpan Start { get => _position; init => _position = value; }
        public int Writes { get; private set; }
        public bool IsParkedOnFrame(TimeSpan target) => Parked;
        public int Freezes { get; private set; }
        public void FreezeOnFrame(TimeSpan target) => Freezes++;
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

        public void WritePosition(TimeSpan target) { Writes++; _position = target; }
        public void CommitPaused(TimeSpan position) => IsPaused = true;
        public void CommitPlaying(TimeSpan position, string seekId)
        {
            Assert.True(Presented || Parked);
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
