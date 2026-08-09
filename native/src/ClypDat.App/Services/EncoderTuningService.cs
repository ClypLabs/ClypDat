using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Observes live capture health and reports what an encoder preset WOULD be
// under real load. Resolution and frame rate are user-controlled settings and
// are never changed by this service.
public sealed class EncoderTuningService
{
    // Retained for source compatibility with older integrations. The service
    // is advisory-only and never raises either mutation event.
#pragma warning disable CS0067 // Retained as dormant compatibility surface.
    public event EventHandler<EncoderFrameRateChange>? FrameRateChangeRequested;
    public event EventHandler<EncoderResolutionChange>? ResolutionChangeRequested;
#pragma warning restore CS0067

    private static readonly string[] PresetLadder = { "P5", "P4", "P3", "P2", "P1" };
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PromoteAfterClean = TimeSpan.FromMinutes(10);
    private const int WindowSize = 15;
    private const int DemoteThreshold = 8;
    private const double SeverityOutputFrameRateFraction = 0.7;

    private readonly List<bool> _recentOverloads = new();
    private readonly HashSet<string> _burnedPresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _demotionsPerPreset = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _sessionStartUtc = DateTime.MinValue;
    private DateTime _lastDecisionUtc = DateTime.MinValue;
    private DateTime? _cleanSinceUtc;
    private int _queueDepthSinceClean;
    private string _proposedPreset = string.Empty;
    private string _ceilingPreset = string.Empty;
    private int _configuredFrameRate;
    private int _configuredHeight;
    private int _samplesSeen;
    private int _overloadedSamplesSeen;
    private int _severeSamplesSeen;

    public void BeginSession(string userPreset, int configuredFrameRate, int configuredHeight)
    {
        _sessionStartUtc = DateTime.UtcNow;
        _lastDecisionUtc = DateTime.MinValue;
        _cleanSinceUtc = null;
        _queueDepthSinceClean = 0;
        _samplesSeen = 0;
        _overloadedSamplesSeen = 0;
        _severeSamplesSeen = 0;
        _recentOverloads.Clear();
        _ceilingPreset = Normalize(userPreset);
        _proposedPreset = _ceilingPreset;
        _configuredFrameRate = configuredFrameRate;
        _configuredHeight = configuredHeight;
        AppLog.Info($"Encoder tuning: observing session at preset {_ceilingPreset}, {configuredFrameRate} fps, {configuredHeight}p (all capture settings remain user-controlled).");
    }

    public void EndSession()
    {
        if (_sessionStartUtc == DateTime.MinValue) return;

        AppLog.Info($"Encoder tuning: session ended after {_samplesSeen} usable sample(s), " +
                    $"{_overloadedSamplesSeen} overloaded ({_severeSamplesSeen} severe). " +
                    (string.Equals(_proposedPreset, _ceilingPreset, StringComparison.OrdinalIgnoreCase)
                        ? $"No change proposed to the configured {_ceilingPreset}."
                        : $"Would have run {_proposedPreset} instead of the configured {_ceilingPreset}.") +
                    $" Settings remain {_configuredFrameRate} fps at {_configuredHeight}p.");

        _sessionStartUtc = DateTime.MinValue;
    }

    public void OnHealth(ReplayCaptureHealth health)
    {
        if (_sessionStartUtc == DateTime.MinValue) return;
        if (health.EncodeQueueCapacity <= 0 || string.IsNullOrEmpty(health.EncoderPreset)) return;
        if (health.State is not (ReplayCaptureState.Healthy or ReplayCaptureState.Degraded)) return;
        if (health.DegradeReason is ReplayDegradeReason.CaptureStall) return;
        if (health.SaveInProgress) return;

        var now = health.UpdatedUtc;
        if (now - _sessionStartUtc < Warmup) return;

        var flagged = health.DegradeReason == ReplayDegradeReason.EncoderOverload;
        var severe = flagged && health.TargetFrameRate > 0 &&
                     health.OutputFrameRate < health.TargetFrameRate * SeverityOutputFrameRateFraction;
        _samplesSeen++;
        if (flagged) _overloadedSamplesSeen++;
        if (severe) _severeSamplesSeen++;

        _recentOverloads.Add(severe);
        if (_recentOverloads.Count > WindowSize) _recentOverloads.RemoveAt(0);

        if (severe)
        {
            _cleanSinceUtc = null;
            _queueDepthSinceClean = 0;
        }
        else
        {
            _cleanSinceUtc ??= now;
            _queueDepthSinceClean = Math.Max(_queueDepthSinceClean, health.QueueDepth);
        }

        if (now - _lastDecisionUtc < Cooldown) return;

        var overloadCount = _recentOverloads.Count(entry => entry);
        if (severe && _recentOverloads.Count >= WindowSize && overloadCount >= DemoteThreshold)
        {
            ProposeDemotion(health, now, overloadCount);
            return;
        }

        ProposePromotionIfEarned(health, now);
    }

    private void ProposeDemotion(ReplayCaptureHealth health, DateTime now, int overloadCount)
    {
        var next = Step(_proposedPreset, +1);
        if (next is null)
        {
            var encoderIsBehind = health.EncodeQueueCapacity > 0 &&
                                  health.QueueDepth * 2 >= health.EncodeQueueCapacity;
            if (!encoderIsBehind)
            {
                AppLog.Info($"Encoder tuning: output short of target ({health.OutputFrameRate:0.0}/{health.TargetFrameRate}) but the encode queue is " +
                            $"{health.QueueDepth}/{health.EncodeQueueCapacity} - the encoder is keeping up. Leaving the configured " +
                            $"{_configuredFrameRate} fps at {_configuredHeight}p unchanged.");
            }
            else
            {
                AppLog.Info($"Encoder tuning: sustained overload at {_proposedPreset}, already at the fastest preset, " +
                            $"configured {_configuredFrameRate} fps and {_configuredHeight}p - {overloadCount}/{WindowSize} windows severely overloaded, " +
                            $"dropped={health.DroppedFrames}, queue={health.QueueDepth}/{health.EncodeQueueCapacity}, " +
                            $"outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}. Leaving the configured capture settings unchanged.");
            }

            _lastDecisionUtc = now;
            _recentOverloads.Clear();
            return;
        }

        _demotionsPerPreset.TryGetValue(_proposedPreset, out var demotions);
        _demotionsPerPreset[_proposedPreset] = demotions + 1;
        if (demotions + 1 >= 2 && _burnedPresets.Add(_proposedPreset))
        {
            AppLog.Info($"Encoder tuning: {_proposedPreset} demoted twice this run - will not propose returning to it.");
        }

        AppLog.Info($"Encoder tuning: WOULD DEMOTE {_proposedPreset} -> {next} - " +
                    $"{overloadCount}/{WindowSize} windows severely overloaded, dropped={health.DroppedFrames}, " +
                    $"queue={health.QueueDepth}/{health.EncodeQueueCapacity}, " +
                    $"outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}, adapter={health.AdapterDescription}. " +
                    "Configured resolution and frame rate remain unchanged.");

        _proposedPreset = next;
        _lastDecisionUtc = now;
        _recentOverloads.Clear();
        _cleanSinceUtc = null;
        _queueDepthSinceClean = 0;
    }

    private void ProposePromotionIfEarned(ReplayCaptureHealth health, DateTime now)
    {
        if (_cleanSinceUtc is null || now - _cleanSinceUtc < PromoteAfterClean) return;
        if (health.EncodeQueueCapacity > 0 && _queueDepthSinceClean * 4 >= health.EncodeQueueCapacity) return;

        var next = Step(_proposedPreset, -1);
        if (next is null || IndexOf(next) < IndexOf(_ceilingPreset)) return;
        if (_burnedPresets.Contains(next)) return;
        if (health.EncodeQueueCapacity > 0 && _queueDepthSinceClean * 4 >= health.EncodeQueueCapacity) return;

        AppLog.Info($"Encoder tuning: WOULD PROMOTE {_proposedPreset} -> {next} - " +
                    $"clean for {(now - _cleanSinceUtc.Value).TotalMinutes:0.0} min, " +
                    $"peak queue since clean {_queueDepthSinceClean}/{health.EncodeQueueCapacity}, " +
                    $"outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}. " +
                    "Configured resolution and frame rate remain unchanged.");

        _proposedPreset = next;
        _lastDecisionUtc = now;
        _cleanSinceUtc = null;
        _queueDepthSinceClean = 0;
    }

    private static string? Step(string preset, int direction)
    {
        var index = IndexOf(preset) + direction;
        return index >= 0 && index < PresetLadder.Length ? PresetLadder[index] : null;
    }

    private static int IndexOf(string preset)
    {
        var index = Array.FindIndex(PresetLadder, entry => string.Equals(entry, preset, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : Array.IndexOf(PresetLadder, "P4");
    }

    private static string Normalize(string preset) => PresetLadder[IndexOf(preset)];
}

public sealed record EncoderFrameRateChange(int PreviousFrameRate, int FrameRate);

public sealed record EncoderResolutionChange(int PreviousHeight, int Height);
