using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Watches live capture health and works out what the encoder preset SHOULD be
// for this machine under real load.
//
// Deliberately observe-only for now: it logs the decision it would have made
// and never changes a setting. The thresholds below are calibrated against one
// machine's numbers (see NativeReplayBuffer's preset comment - p4 measured
// 16-28ms/frame and dropped 110 frames in a 2s window under real gameplay,
// where the same preset looks perfectly fine on an idle desktop), and applying
// a preset change costs a full buffer restart. Both are good reasons to let it
// run against real sessions and check its proposals against what actually
// happened before it is allowed to act on them.
//
// Only the Native backend reports the telemetry this needs; other backends are
// ignored rather than guessed at.
public sealed class EncoderTuningService
{
    // Slowest/best-looking first. "Demote" moves toward P1 (cheaper per frame),
    // "promote" moves back toward the user's own setting.
    private static readonly string[] PresetLadder = { "P5", "P4", "P3", "P2", "P1" };

    // Encoder and driver warm-up, shader compilation and level loading all
    // produce overload that says nothing about the steady state, and they all
    // land right after a buffer start.
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(30);
    // A restart plus another warm-up has to finish before another decision
    // could mean anything.
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    // Promotion is far more cautious than demotion on purpose. A needless
    // demotion costs some compression quality; a needless promotion costs
    // dropped frames, which is content the user can never get back.
    private static readonly TimeSpan PromoteAfterClean = TimeSpan.FromMinutes(10);

    // Health arrives every ~2s, so this is a 10s window. One bad window is an
    // alt-tab or a loading screen; six seconds out of ten is the machine
    // actually failing to keep up.
    private const int WindowSize = 5;
    private const int DemoteThreshold = 3;

    private readonly List<bool> _recentOverloads = new();
    private readonly HashSet<string> _burnedPresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _demotionsPerPreset = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _sessionStartUtc = DateTime.MinValue;
    private DateTime _lastDecisionUtc = DateTime.MinValue;
    private DateTime? _cleanSinceUtc;
    private int _queueDepthSinceClean;
    private string _proposedPreset = string.Empty;
    private string _ceilingPreset = string.Empty;
    private int _samplesSeen;
    private int _overloadedSamplesSeen;

    // Called on every buffer start. The burned-preset set deliberately survives
    // within a run of the app but the streak state does not - a fresh buffer is
    // a fresh set of conditions (different game, different resolution).
    public void BeginSession(string userPreset)
    {
        _sessionStartUtc = DateTime.UtcNow;
        _lastDecisionUtc = DateTime.MinValue;
        _cleanSinceUtc = null;
        _queueDepthSinceClean = 0;
        _samplesSeen = 0;
        _overloadedSamplesSeen = 0;
        _recentOverloads.Clear();
        _ceilingPreset = Normalize(userPreset);
        _proposedPreset = _ceilingPreset;
        AppLog.Info($"Encoder tuning: observing session at preset {_ceilingPreset} (observe-only, no settings will change).");
    }

    public void EndSession()
    {
        if (_sessionStartUtc == DateTime.MinValue) return;
        // Always report the sample count, not just the verdict. A tuner that
        // saw nothing and a tuner that saw a healthy session both say nothing
        // otherwise, and the first time this ran every event was being
        // discarded before it was counted - which read exactly like "no
        // problems found" in the log.
        AppLog.Info($"Encoder tuning: session ended after {_samplesSeen} usable sample(s), " +
                    $"{_overloadedSamplesSeen} overloaded. " +
                    (string.Equals(_proposedPreset, _ceilingPreset, StringComparison.OrdinalIgnoreCase)
                        ? $"No change proposed to the configured {_ceilingPreset}."
                        : $"Would have run {_proposedPreset} instead of the configured {_ceilingPreset}."));

        _sessionStartUtc = DateTime.MinValue;
    }

    public void OnHealth(ReplayCaptureHealth health)
    {
        if (_sessionStartUtc == DateTime.MinValue) return;
        // Capability check, NOT a check on Backend's name. Under the default
        // Auto backend the buffer is a HybridReplayBuffer wrapping the native
        // engine, and it relabels every record it forwards as "Hybrid" - so
        // matching on "Native" silently discarded every event and the tuner sat
        // mute through sessions that dropped a hundred frames a window. These
        // two fields are only ever populated by the native engine, so asking
        // whether the telemetry this needs is actually present survives any
        // wrapper renaming the record on its way here.
        if (health.EncodeQueueCapacity <= 0 || string.IsNullOrEmpty(health.EncoderPreset)) return;
        if (health.State is not (ReplayCaptureState.Healthy or ReplayCaptureState.Degraded)) return;
        // A stall is the display refusing to hand over frames - the encoder is
        // idle and blameless, and demoting for it would ratchet a machine down
        // to P1 for something a faster preset cannot fix.
        if (health.DegradeReason == ReplayDegradeReason.CaptureStall) return;

        var now = health.UpdatedUtc;
        if (now - _sessionStartUtc < Warmup) return;

        var overloaded = health.DegradeReason == ReplayDegradeReason.EncoderOverload;
        _samplesSeen++;
        if (overloaded) _overloadedSamplesSeen++;

        _recentOverloads.Add(overloaded);
        if (_recentOverloads.Count > WindowSize) _recentOverloads.RemoveAt(0);

        if (overloaded)
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
        if (_recentOverloads.Count >= WindowSize && overloadCount >= DemoteThreshold)
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
            AppLog.Info($"Encoder tuning: sustained overload at {_proposedPreset}, already at the fastest preset - " +
                        $"{overloadCount}/{WindowSize} windows overloaded, dropped={health.DroppedFrames}, " +
                        $"queue={health.QueueDepth}/{health.EncodeQueueCapacity}, outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}. " +
                        "Resolution or frame rate is the remaining lever, not the preset.");
            _lastDecisionUtc = now;
            return;
        }

        // Twice down off the same preset in one run means it is not merely
        // marginal here - stop letting promotion hand it back.
        _demotionsPerPreset.TryGetValue(_proposedPreset, out var demotions);
        _demotionsPerPreset[_proposedPreset] = demotions + 1;
        if (demotions + 1 >= 2 && _burnedPresets.Add(_proposedPreset))
        {
            AppLog.Info($"Encoder tuning: {_proposedPreset} demoted twice this run - will not propose returning to it.");
        }

        AppLog.Info($"Encoder tuning: WOULD DEMOTE {_proposedPreset} -> {next} - " +
                    $"{overloadCount}/{WindowSize} windows overloaded, dropped={health.DroppedFrames}, " +
                    $"queue={health.QueueDepth}/{health.EncodeQueueCapacity}, " +
                    $"outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}, adapter={health.AdapterDescription}.");

        _proposedPreset = next;
        _lastDecisionUtc = now;
        _recentOverloads.Clear();
        _cleanSinceUtc = null;
        _queueDepthSinceClean = 0;
    }

    private void ProposePromotionIfEarned(ReplayCaptureHealth health, DateTime now)
    {
        if (_cleanSinceUtc is null || now - _cleanSinceUtc < PromoteAfterClean) return;
        // Never above what the user actually asked for - the tuner's job is to
        // rescue a setting that cannot keep up, not to overrule the choice.
        var next = Step(_proposedPreset, -1);
        if (next is null || IndexOf(next) < IndexOf(_ceilingPreset)) return;
        if (_burnedPresets.Contains(next)) return;
        // Headroom, not merely the absence of drops: a queue that has been
        // running close to full is one stutter away from dropping again.
        if (health.EncodeQueueCapacity > 0 && _queueDepthSinceClean * 4 >= health.EncodeQueueCapacity)
        {
            return;
        }

        AppLog.Info($"Encoder tuning: WOULD PROMOTE {_proposedPreset} -> {next} - " +
                    $"clean for {(now - _cleanSinceUtc.Value).TotalMinutes:0.0} min, " +
                    $"peak queue since clean {_queueDepthSinceClean}/{health.EncodeQueueCapacity}, " +
                    $"outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}.");

        _proposedPreset = next;
        _lastDecisionUtc = now;
        _cleanSinceUtc = null;
        _queueDepthSinceClean = 0;
    }

    // direction +1 steps toward P1 (cheaper), -1 toward P5 (better looking).
    private static string? Step(string preset, int direction)
    {
        var index = IndexOf(preset) + direction;
        return index >= 0 && index < PresetLadder.Length ? PresetLadder[index] : null;
    }

    private static int IndexOf(string preset)
    {
        var index = Array.FindIndex(PresetLadder, entry => string.Equals(entry, preset, StringComparison.OrdinalIgnoreCase));
        // Matches NvencPreset's own fallback, so an unrecognised value lands
        // where the encoder would actually have run it.
        return index >= 0 ? index : Array.IndexOf(PresetLadder, "P4");
    }

    private static string Normalize(string preset) => PresetLadder[IndexOf(preset)];
}
