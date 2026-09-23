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
    public async Task Startup_DelayedAudio_HoldsPosterAndVideoUntilAudioIsReady()
    {
        var preparation = new TaskCompletionSource<AudioPreparationResult>();
        var transport = new RecoveryTransport(preparation.Task);
        // Long enough that a loaded test run cannot outlast it before the
        // result is set below.
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            startAudioBudget: TimeSpan.FromSeconds(30));

        var startup = coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => true, CancellationToken.None);

        await Task.Delay(20);
        Assert.Equal(0, transport.AudioStarts);
        Assert.Equal(0, transport.Reveals);
        Assert.True(transport.Presented);
        Assert.True(transport.IsPaused);
        preparation.SetResult(new AudioPreparationResult(1, 0, false));
        var result = await startup;
        Assert.Equal(EditorPlaybackStartOutcome.Playing, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Landed);
        Assert.Equal(1, transport.AudioStarts);
        Assert.False(transport.IsPaused);
        Assert.Equal(["write", "reveal", "play", "clock", "audio"], transport.Calls);
    }

    [Fact]
    public async Task Startup_AudioReady_StartsAudioOnlyAfterVideoMoves()
    {
        var transport = new RecoveryTransport();
        var coordinator = new EditorSeekCoordinator(pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["write", "reveal", "play", "clock", "audio"], transport.Calls);
        // Anchored where the moving video is, not where it was parked.
        Assert.True(transport.AudioAnchor > result.Landed);
        Assert.Equal(result.Anchor, transport.AudioAnchor);
        Assert.Equal(result.Anchor, transport.ClockAnchor);
    }

    [Fact]
    public async Task Startup_AudioMissesBudget_PlaysVideoThenJoinsAudioOnce()
    {
        var preparation = new TaskCompletionSource<AudioPreparationResult>();
        var transport = new RecoveryTransport(preparation.Task);
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            startAudioBudget: TimeSpan.FromMilliseconds(15));

        var result = await coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => true, CancellationToken.None);

        Assert.Equal(EditorPlaybackStartOutcome.AudioPending, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.False(transport.IsPaused);
        Assert.Equal(0, transport.AudioStarts);
        preparation.SetResult(new AudioPreparationResult(1, 0, false));
        Assert.True(SpinWait.SpinUntil(() => transport.AudioStarts == 1, TimeSpan.FromSeconds(5)));
        await Task.Delay(10);
        Assert.Equal(1, transport.AudioStarts);
    }

    [Fact]
    public async Task Startup_VideoNeverRolls_RevealsPausedWithoutAudio()
    {
        var transport = new RecoveryTransport(videoRolls: false);
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            rollTimeout: TimeSpan.FromMilliseconds(12));

        var result = await coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => true, CancellationToken.None);

        Assert.Equal(EditorPlaybackStartOutcome.RevealedPaused, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(1, transport.Reveals);
        Assert.True(transport.IsPaused);
        Assert.Equal(0, transport.AudioStarts);
    }

    [Fact]
    public async Task Startup_SupersededDuringAudioWait_ReturnsWithoutWaitingOutTheBudget()
    {
        var preparation = new TaskCompletionSource<AudioPreparationResult>();
        var transport = new RecoveryTransport(preparation.Task);
        var coordinator = new EditorSeekCoordinator(
            pollInterval: TimeSpan.FromMilliseconds(1),
            startAudioBudget: TimeSpan.FromSeconds(30));
        var current = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var startup = coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => current, CancellationToken.None);
        await Task.Delay(10);
        current = false;
        var result = await startup;

        Assert.True(result.Superseded);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(0, transport.Reveals);
        Assert.Equal(0, transport.AudioStarts);
    }

    [Fact]
    public async Task Startup_RevealRejected_NeitherPlaysNorStartsAudio()
    {
        var transport = new RecoveryTransport { RevealResult = false };
        var coordinator = new EditorSeekCoordinator(pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => true, CancellationToken.None);

        Assert.True(result.Superseded);
        Assert.True(transport.IsPaused);
        Assert.DoesNotContain("play", transport.Calls);
        Assert.Equal(0, transport.AudioStarts);
    }

    [Fact]
    public async Task Startup_WarmFrame_IsRevealedWithoutAnotherSeek()
    {
        var transport = new RecoveryTransport { ReusesPresentedFrame = true, Start = TimeSpan.FromSeconds(10) };
        var coordinator = new EditorSeekCoordinator(pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, transport.Writes);
        Assert.Equal(["reveal", "play", "clock", "audio"], transport.Calls);
    }

    [Fact]
    public async Task Startup_SilentSource_StartsVideoWithoutAudioWait()
    {
        var transport = new RecoveryTransport(Task.FromResult(new AudioPreparationResult(0, 0, false)));
        var coordinator = new EditorSeekCoordinator(pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => true, CancellationToken.None);

        Assert.Equal(EditorPlaybackStartOutcome.Playing, result.Outcome);
        Assert.Equal(0, transport.AudioStarts);
        Assert.False(transport.IsPaused);
    }

    [Fact]
    public async Task Startup_FaultedAudioLoad_PlaysSilentInsteadOfFailing()
    {
        var transport = new RecoveryTransport(Task.FromException<AudioPreparationResult>(new InvalidOperationException("no device")));
        var coordinator = new EditorSeekCoordinator(pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await coordinator.StartAsync(transport, TimeSpan.FromSeconds(10), "start", () => true, CancellationToken.None);

        Assert.Equal(EditorPlaybackStartOutcome.Playing, result.Outcome);
        Assert.Equal(0, transport.AudioStarts);
        Assert.False(transport.IsPaused);
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
        private ulong _picture;
        public List<string> Calls { get; } = [];
        public bool ReusesPresentedFrame { get; init; }
        public bool CanReusePresentedFrame(TimeSpan target) => ReusesPresentedFrame;
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

        public bool IsPaused { get; private set; } = true;
        public TimeSpan Position => _position;
        public TimeSpan LivePosition => _position;
        public ulong PresentedPicture => _picture;
        public bool RevealResult { get; init; } = true;
        public int Reveals { get; private set; }
        public TimeSpan AudioAnchor { get; private set; }
        public TimeSpan ClockAnchor { get; private set; }

        public Task<bool> RevealAsync(CancellationToken token)
        {
            Calls.Add("reveal");
            Reveals++;
            return Task.FromResult(RevealResult);
        }

        public void PlayVideo()
        {
            Calls.Add("play");
            IsPaused = false;
            if (!_videoRolls) return;
            _picture++;
            _position += TimeSpan.FromMilliseconds(25);
        }

        public void ResumeClock(TimeSpan anchor)
        {
            Calls.Add("clock");
            ClockAnchor = anchor;
        }
        public int AudioTrackCount => 1;
        public double PlaybackRate => 1;
        public string VideoState => IsPaused ? "Paused" : "Playing";
        public bool IsNetworkSource => false;
        public int AudioStarts { get; private set; }

        public Task<AudioPreparationResult> PrepareAudioAsync(TimeSpan target, string seekId, CancellationToken cancellationToken = default) => _preparation;

        public void StopAudio() { }
        public void PauseVideo() => IsPaused = true;
        public void ResetVideo() => IsPaused = true;

        public void WritePosition(TimeSpan target) { Calls.Add("write"); Writes++; _position = target; }
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

        public void StartDeferredAudio(TimeSpan position, string seekId)
        {
            Calls.Add("audio");
            AudioAnchor = position;
            AudioStarts++;
        }

        public void LogDebug(string line) { }
        public void LogInfo(string line) { }
        public void LogError(string line) { }
    }
}
