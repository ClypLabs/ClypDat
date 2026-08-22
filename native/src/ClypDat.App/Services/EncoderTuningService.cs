using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

public sealed class EncoderTuningService
{
#pragma warning disable CS0067
    public event EventHandler<EncoderFrameRateChange>? FrameRateChangeRequested;
    public event EventHandler<EncoderResolutionChange>? ResolutionChangeRequested;
#pragma warning restore CS0067

    private static readonly int[] FrameRateLadder = { 144, 120, 90, 60, 30 };
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RestoreAfterClean = TimeSpan.FromMinutes(10);
    private const int WindowSize = 15;
    private const int DemoteThreshold = 8;
    // A 60 fps capture producing 44 fps with a backed-up queue is already
    // sustained encoder overload, not a harmless near-target wobble. VFR
    // avoids padding source-limited captures, while this guard still protects
    // the genuinely overloaded encoder path.
    private const double SevereOutputFraction = 0.9;
    private const double HealthyOutputFraction = 0.95;
    private const double SevereQueueFraction = 0.5;
    private const double HealthyQueueFraction = 0.25;

    private readonly List<bool> _recentSevere = new();
    private readonly List<double?> _recentOutputs = new();
    private DateTime _sessionStartUtc = DateTime.MinValue;
    private DateTime _lastDecisionUtc = DateTime.MinValue;
    private DateTime? _cleanSinceUtc;
    private int _peakQueueSinceClean;
    private int _configuredFrameRate;
    private int _activeFrameRate;
    private int _configuredHeight;
    private string _configuredProfile = string.Empty;
    private int _samplesSeen;
    private int _severeSamplesSeen;
    private bool _enabled = true;
    private DateTime _enabledSinceUtc = DateTime.MinValue;

    public void BeginSession(string encoderProfile, int configuredFrameRate, int configuredHeight, bool enabled = true)
    {
        _sessionStartUtc = DateTime.UtcNow;
        _lastDecisionUtc = DateTime.MinValue;
        _cleanSinceUtc = null;
        _peakQueueSinceClean = 0;
        _recentSevere.Clear();
        _recentOutputs.Clear();
        _configuredProfile = encoderProfile;
        _configuredFrameRate = Math.Clamp(configuredFrameRate, 30, 144);
        _activeFrameRate = _configuredFrameRate;
        _enabled = enabled;
        _enabledSinceUtc = enabled ? _sessionStartUtc : DateTime.MaxValue;
        _configuredHeight = configuredHeight;
        _samplesSeen = 0;
        _severeSamplesSeen = 0;
        AppLog.Info($"Encoder tuning: monitoring {_configuredProfile}, {_configuredFrameRate} fps, {_configuredHeight}p.");
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        _enabledSinceUtc = enabled ? DateTime.UtcNow : DateTime.MaxValue;
        _recentSevere.Clear();
        _recentOutputs.Clear();
        _cleanSinceUtc = null;
        _peakQueueSinceClean = 0;

        if (!enabled && _sessionStartUtc != DateTime.MinValue && _activeFrameRate != _configuredFrameRate)
        {
            var previous = _activeFrameRate;
            _activeFrameRate = _configuredFrameRate;
            AppLog.Info($"Encoder tuning: automatic FPS adjustment disabled; restoring {previous} -> {_configuredFrameRate} fps.");
            FrameRateChangeRequested?.Invoke(this, new EncoderFrameRateChange(previous, _configuredFrameRate));
        }

        AppLog.Info($"Encoder tuning: automatic FPS adjustment {(enabled ? "enabled" : "disabled")}.");
    }

    public void EndSession()
    {
        if (_sessionStartUtc == DateTime.MinValue) return;

        AppLog.Info($"Encoder tuning: session ended after {_samplesSeen} usable sample(s), " +
                    $"{_severeSamplesSeen} severe. Configured {_configuredFrameRate} fps at {_configuredHeight}p; " +
                    $"active target {_activeFrameRate} fps.");
        _sessionStartUtc = DateTime.MinValue;
    }

    public void OnHealth(ReplayCaptureHealth health)
    {
        if (_sessionStartUtc == DateTime.MinValue) return;
        if (!_enabled) return;
        if (health.EncodeQueueCapacity <= 0 || string.IsNullOrEmpty(health.EncoderProfile)) return;
        if (health.State is not (ReplayCaptureState.Healthy or ReplayCaptureState.Degraded)) return;
        if (health.DegradeReason is ReplayDegradeReason.CaptureStall || health.SaveInProgress) return;

        var now = health.UpdatedUtc;
        if (now - _sessionStartUtc < Warmup) return;
        if (_enabledSinceUtc != DateTime.MinValue && now - _enabledSinceUtc < Warmup) return;
        if (health.TargetFrameRate <= 0 || health.OutputFrameRate <= 0) return;

        var queueFraction = (double)health.QueueDepth / health.EncodeQueueCapacity;
        var encoderOverload = health.DegradeReason == ReplayDegradeReason.EncoderOverload;
        var severe = encoderOverload &&
                     health.OutputFrameRate < health.TargetFrameRate * SevereOutputFraction &&
                     queueFraction >= SevereQueueFraction;
        var clean = health.State == ReplayCaptureState.Healthy &&
                    health.DegradeReason == ReplayDegradeReason.None &&
                    health.OutputFrameRate >= health.TargetFrameRate * HealthyOutputFraction &&
                    queueFraction < HealthyQueueFraction;

        _samplesSeen++;
        if (severe)
        {
            _severeSamplesSeen++;
        }
        _recentSevere.Add(severe);
        _recentOutputs.Add(severe ? health.OutputFrameRate : null);
        if (_recentSevere.Count > WindowSize) _recentSevere.RemoveAt(0);
        if (_recentOutputs.Count > WindowSize) _recentOutputs.RemoveAt(0);

        if (clean)
        {
            _cleanSinceUtc ??= now;
            _peakQueueSinceClean = Math.Max(_peakQueueSinceClean, health.QueueDepth);
        }
        else
        {
            _cleanSinceUtc = null;
            _peakQueueSinceClean = 0;
        }

        if (now - _lastDecisionUtc < Cooldown) return;

        var severeCount = _recentSevere.Count(entry => entry);
        if (severe && _recentSevere.Count >= WindowSize && severeCount >= DemoteThreshold)
        {
            ProposeFrameRateReduction(health, now, severeCount);
            return;
        }

        ProposeFrameRateRestore(health, now);
    }

    private void ProposeFrameRateReduction(ReplayCaptureHealth health, DateTime now, int severeCount)
    {
        if (_activeFrameRate <= 30)
        {
            AppLog.Info($"Encoder tuning: sustained overload at 30 fps floor; queue={health.QueueDepth}/{health.EncodeQueueCapacity}, " +
                        $"outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}.");
            _lastDecisionUtc = now;
            _recentSevere.Clear();
            _recentOutputs.Clear();
            return;
        }

        var measuredCapacity = _recentOutputs
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ElementAt(_recentSevere.Count(entry => entry) / 2);
        var next = FrameRateLadder.FirstOrDefault(rate => rate < _activeFrameRate && rate <= measuredCapacity);
        if (next == 0) next = 30;

        var previous = _activeFrameRate;
        _activeFrameRate = next;
        AppLog.Info($"Encoder tuning: lowering target frame rate {previous} -> {next} fps. " +
                    $"{severeCount}/{WindowSize} windows severe, measured capacity={measuredCapacity:0.0}, " +
                    $"queue={health.QueueDepth}/{health.EncodeQueueCapacity}, " +
                    $"outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}.");
        FrameRateChangeRequested?.Invoke(this, new EncoderFrameRateChange(previous, next));
        _lastDecisionUtc = now;
        _recentSevere.Clear();
        _recentOutputs.Clear();
        _cleanSinceUtc = null;
        _peakQueueSinceClean = 0;
    }

    private void ProposeFrameRateRestore(ReplayCaptureHealth health, DateTime now)
    {
        if (_activeFrameRate >= _configuredFrameRate || _cleanSinceUtc is null) return;
        if (now - _cleanSinceUtc < RestoreAfterClean) return;
        if (_peakQueueSinceClean >= health.EncodeQueueCapacity * HealthyQueueFraction) return;

        var previous = _activeFrameRate;
        _activeFrameRate = _configuredFrameRate;
        AppLog.Info($"Encoder tuning: restoring target frame rate {previous} -> {_configuredFrameRate} fps after " +
                    $"{(now - _cleanSinceUtc.Value).TotalMinutes:0.0} clean minutes.");
        FrameRateChangeRequested?.Invoke(this, new EncoderFrameRateChange(previous, _configuredFrameRate));
        _lastDecisionUtc = now;
        _cleanSinceUtc = null;
        _peakQueueSinceClean = 0;
    }
}

public sealed record EncoderFrameRateChange(int PreviousFrameRate, int FrameRate);

public sealed record EncoderResolutionChange(int PreviousHeight, int Height);
