using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class EditorSeekCoordinatorTests
{
    [Fact]
    public async Task Resume_HoldsAudioUntilPausedLandedAndVideoRolls()
    {
        var transport = new FakeTransport(TimeSpan.Zero)
        {
            PauseAfterCalls = 2,
            LandAfterWrites = 1,
            RollAfterCalls = 2
        };
        var coordinator = FastCoordinator();

        var result = await coordinator.SeekAsync(transport, TimeSpan.FromSeconds(5), resume: true, 7, () => true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Resumed);
        Assert.Equal(TimeSpan.FromMilliseconds(5_020), result.AudioAnchor);
        Assert.Equal(new[] { "stop-audio", "pause", "pause", "write:5000", "resume", "pause", "resume", "anchor:5020", "start-audio" }, transport.Events);
    }

    [Fact]
    public async Task StaleZeroPosition_DoesNotCountAsLanding()
    {
        var transport = new FakeTransport(TimeSpan.Zero) { LandAfterWrites = int.MaxValue };
        var coordinator = FastCoordinator();

        var result = await coordinator.SeekAsync(transport, TimeSpan.FromSeconds(5), resume: true, 1, () => true, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.Resumed);
        Assert.Equal(4, transport.PositionWrites);
        Assert.DoesNotContain("start-audio", transport.Events);
        Assert.Equal("pause", transport.Events[^1]);
    }

    [Fact]
    public async Task Retry_LandsOnSecondAttempt()
    {
        var transport = new FakeTransport(TimeSpan.Zero) { LandAfterWrites = 2 };
        var coordinator = FastCoordinator();

        var result = await coordinator.SeekAsync(transport, TimeSpan.FromSeconds(5), resume: false, 1, () => true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Resumed);
        Assert.Equal(2, transport.PositionWrites);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Landed);
        Assert.DoesNotContain("start-audio", transport.Events);
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(8, 3)]
    public async Task Landing_AcceptsForwardAndBackwardTarget(int initialSeconds, int targetSeconds)
    {
        var transport = new FakeTransport(TimeSpan.FromSeconds(initialSeconds)) { LandAfterWrites = 1 };
        var coordinator = FastCoordinator();

        var result = await coordinator.SeekAsync(transport, TimeSpan.FromSeconds(targetSeconds), resume: false, 1, () => true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(targetSeconds), result.Landed);
    }

    [Fact]
    public async Task SupersededSeek_StopsBeforeWritingTransport()
    {
        var transport = new FakeTransport(TimeSpan.Zero);
        var coordinator = FastCoordinator();

        var result = await coordinator.SeekAsync(transport, TimeSpan.FromSeconds(5), resume: true, 2, () => false, CancellationToken.None);

        Assert.True(result.Superseded);
        Assert.Empty(transport.Events);
    }

    [Fact]
    public async Task Cancellation_LeavesTransportPausedAndSilent()
    {
        var transport = new FakeTransport(TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FastCoordinator().SeekAsync(transport, TimeSpan.FromSeconds(5), resume: true, 1, () => true, cancellation.Token));

        Assert.DoesNotContain("start-audio", transport.Events);
        Assert.Equal("pause", transport.Events[^1]);
    }

    [Fact]
    public async Task StressedPreview_ProactivelyResetsBeforeLanding()
    {
        var transport = new FakeTransport(TimeSpan.Zero);
        var result = await FastCoordinator().SeekAsync(transport, TimeSpan.FromSeconds(5), false, 1, () => true, CancellationToken.None, resetBeforeSeek: true);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "stop-audio", "reset", "pause", "write:5000" }, transport.Events);
    }

    [Fact]
    public async Task LandingFailure_ResetsAndRetriesOnce()
    {
        var transport = new FakeTransport(TimeSpan.Zero) { LandAfterWrites = 3 };
        var result = await FastCoordinator().SeekAsync(transport, TimeSpan.FromSeconds(5), false, 1, () => true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, transport.ResetCalls);
        Assert.Equal(3, transport.PositionWrites);
    }

    [Fact]
    public async Task FailedRecovery_RemainsPausedAndSilent()
    {
        var transport = new FakeTransport(TimeSpan.Zero) { LandAfterWrites = int.MaxValue };
        var result = await FastCoordinator().SeekAsync(transport, TimeSpan.FromSeconds(5), true, 1, () => true, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, transport.ResetCalls);
        Assert.DoesNotContain("start-audio", transport.Events);
        Assert.Equal("pause", transport.Events[^1]);
    }

    [Fact]
    public async Task RollFailure_ResetsAndResumesAfterRecovery()
    {
        var transport = new FakeTransport(TimeSpan.Zero) { RollAfterCalls = 3 };
        var result = await FastCoordinator().SeekAsync(transport, TimeSpan.FromSeconds(5), true, 1, () => true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Resumed);
        Assert.Equal(1, transport.ResetCalls);
        Assert.Contains("start-audio", transport.Events);
    }

    [Fact]
    public async Task SupersededRecovery_DoesNotRetryOrStartAudio()
    {
        var transport = new FakeTransport(TimeSpan.Zero) { LandAfterWrites = int.MaxValue };
        var result = await FastCoordinator().SeekAsync(
            transport, TimeSpan.FromSeconds(5), true, 1,
            () => transport.ResetCalls == 0, CancellationToken.None);

        Assert.True(result.Superseded);
        Assert.Equal(1, transport.ResetCalls);
        Assert.DoesNotContain("start-audio", transport.Events);
    }

    [Fact]
    public void AudioClock_ConvertsHardwarePositionAndCorrectsOnlyOnce()
    {
        var policy = new EditorAvClockPolicy();
        policy.Begin(4);
        var audible = EditorAvClockPolicy.ToMediaTime(TimeSpan.FromSeconds(10), 1_000, 385_000, 48_000 * 8);
        Assert.Equal(TimeSpan.FromSeconds(11), audible);

        Assert.False(policy.TryGetCorrection(4, TimeSpan.FromMilliseconds(200), audible, TimeSpan.FromSeconds(11.3), out _));
        Assert.False(policy.TryGetCorrection(4, TimeSpan.FromMilliseconds(300), audible, TimeSpan.FromSeconds(11.3), out _));
        Assert.True(policy.TryGetCorrection(4, TimeSpan.FromMilliseconds(400), audible, TimeSpan.FromSeconds(11.3), out var correction));
        Assert.Equal(TimeSpan.FromSeconds(11.3), correction);
        Assert.False(policy.TryGetCorrection(4, TimeSpan.FromMilliseconds(500), audible, TimeSpan.FromSeconds(10.7), out _));
        Assert.False(policy.TryGetCorrection(3, TimeSpan.FromMilliseconds(500), audible, TimeSpan.FromSeconds(11.3), out _));
    }

    [Fact]
    public void AudioClock_CorrectsAudioAheadAfterTwoConsistentSamples()
    {
        var policy = new EditorAvClockPolicy();
        policy.Begin(5);
        var audible = TimeSpan.FromSeconds(10);
        var video = TimeSpan.FromSeconds(9.7);

        Assert.False(policy.TryGetCorrection(5, TimeSpan.FromMilliseconds(300), audible, video, out _));
        Assert.True(policy.TryGetCorrection(5, TimeSpan.FromMilliseconds(400), audible, video, out var correction));
        Assert.Equal(video, correction);
    }

    private static EditorSeekCoordinator FastCoordinator() =>
        new(TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

    private sealed class FakeTransport : IEditorSeekTransport
    {
        private TimeSpan _position;
        private TimeSpan _target;
        private int _pauseCalls;
        private int _resumeCalls;

        public FakeTransport(TimeSpan position) => _position = position;
        public List<string> Events { get; } = [];
        public int PauseAfterCalls { get; init; } = 1;
        public int LandAfterWrites { get; init; } = 1;
        public int RollAfterCalls { get; init; } = 1;
        public int PositionWrites { get; private set; }
        public int ResetCalls { get; private set; }
        public bool IsPaused => _pauseCalls >= PauseAfterCalls;
        public TimeSpan Position
        {
            get
            {
                if (_resumeCalls >= RollAfterCalls) return _target + TimeSpan.FromMilliseconds(20);
                return _position;
            }
        }

        public void StopAudio() => Events.Add("stop-audio");
        public void PauseVideo() { _pauseCalls++; Events.Add("pause"); }
        public void ResetVideo() { ResetCalls++; _pauseCalls = PauseAfterCalls; Events.Add("reset"); }
        public void WritePosition(TimeSpan target)
        {
            PositionWrites++;
            _target = target;
            if (PositionWrites >= LandAfterWrites) _position = target;
            Events.Add($"write:{target.TotalMilliseconds:0}");
        }
        public void ResumeVideo() { _resumeCalls++; Events.Add("resume"); }
        public void AnchorAudio(TimeSpan position) => Events.Add($"anchor:{position.TotalMilliseconds:0}");
        public void StartAudio() => Events.Add("start-audio");
    }
}
