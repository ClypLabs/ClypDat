using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Watches live capture health and works out what the encoder preset SHOULD be
// for this machine under real load.
//
// PRESET changes remain observe-only: it logs the decision it would have made
// and never changes the setting. The thresholds below are calibrated against
// one machine's numbers (see NativeReplayBuffer's preset comment - p4 measured
// 16-28ms/frame and dropped 110 frames in a 2s window under real gameplay,
// where the same preset looks perfectly fine on an idle desktop), and applying
// a preset change costs a full buffer restart. Both are good reasons to let it
// run against real sessions and check its proposals against what actually
// happened before it is allowed to act on them.
//
// FRAME RATE is different, and this DOES act on it. Once the ladder bottoms out
// at P1 there is no preset left to spend, and the previous behaviour was to log
// "resolution or frame rate is the remaining lever" and then do nothing: a real
// session on an Iris Xe laptop sat at 18.8fps against a 60fps target for
// minutes, binning a third of the user's frames, and never said so. Halving the
// target costs no restart at all (see NativeReplayBuffer.RequestFrameRate) and
// turns dropped frames into a lower but honest rate.
//
// Only the Native backend reports the telemetry this needs; other backends are
// ignored rather than guessed at.
public sealed class EncoderTuningService
{
    // What a machine that cannot sustain its configured rate falls back to.
    // Only ever one step: below 30 the clip stops being pleasant to watch, at
    // which point the honest answer is a settings change, not more ratcheting.
    private const int ReducedFrameRate = 30;
    // Below this there is nothing worth halving.
    private const int MinimumFrameRateToReduce = 45;

    // Raised when the target frame rate should change. MainWindow wires this to
    // the live buffer and to the user-facing toast.
    public event EventHandler<EncoderFrameRateChange>? FrameRateChangeRequested;

    private int _configuredFrameRate;
    private int _activeFrameRate;
    // One step down per session. Handing the rate back and taking it away again
    // would be worse than either, and this machine has already shown what it
    // can do.
    private bool _frameRateReduced;

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

    // Health arrives every ~2s, so this is a 30s window needing more than half
    // of it bad. It started at 3-of-5 (10s) and that was far too twitchy: a
    // 5.5-minute session that was healthy for 94% of its samples still got
    // ratcheted to the preset floor, because two short bursts each tripped
    // three consecutive windows. Bursts that brief are loading screens and
    // scene changes, not a machine that cannot sustain the preset.
    private const int WindowSize = 15;
    private const int DemoteThreshold = 8;

    // A window only counts toward demotion if capture actually lost meaningful
    // content in it. The upstream overload flag trips on a SINGLE dropped
    // frame, which does not distinguish a blip from a collapse - measured on
    // the same machine, a genuinely failing session held outputFps at 9.9/60
    // (16% of target) while merely bursty ones sat at 48-52/60 (80-86%), and
    // only the first is worth spending picture quality to fix.
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
    private int _samplesSeen;
    private int _overloadedSamplesSeen;
    private int _severeSamplesSeen;

    // Called on every buffer start. The burned-preset set deliberately survives
    // within a run of the app but the streak state does not - a fresh buffer is
    // a fresh set of conditions (different game, different resolution).
    public void BeginSession(string userPreset, int configuredFrameRate)
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
        _activeFrameRate = configuredFrameRate;
        _frameRateReduced = false;
        AppLog.Info($"Encoder tuning: observing session at preset {_ceilingPreset}, {configuredFrameRate} fps (preset changes are observe-only; frame rate can be lowered if the encoder cannot keep up).");
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
                    $"{_overloadedSamplesSeen} overloaded ({_severeSamplesSeen} severe). " +
                    (string.Equals(_proposedPreset, _ceilingPreset, StringComparison.OrdinalIgnoreCase)
                        ? $"No change proposed to the configured {_ceilingPreset}."
                        : $"Would have run {_proposedPreset} instead of the configured {_ceilingPreset}.") +
                    (_frameRateReduced ? $" Target frame rate was lowered {_configuredFrameRate} -> {_activeFrameRate} fps." : string.Empty));

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

        // Severity rides on top of the upstream flag rather than replacing it:
        // that flag already knows not to cry overload when adaptive frame rate
        // is legitimately encoding below target on an idle screen, which a
        // bare output-vs-target ratio here would get wrong on its own.
        var flagged = health.DegradeReason == ReplayDegradeReason.EncoderOverload;
        var severe = flagged && health.TargetFrameRate > 0 &&
                     health.OutputFrameRate < health.TargetFrameRate * SeverityOutputFrameRateFraction;
        _samplesSeen++;
        if (flagged) _overloadedSamplesSeen++;
        if (severe) _severeSamplesSeen++;

        var overloaded = severe;
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
            // Out of presets. Frame rate is the one remaining lever that can be
            // pulled live - see this class's header and RequestFrameRate.
            if (!_frameRateReduced && _activeFrameRate >= MinimumFrameRateToReduce)
            {
                _frameRateReduced = true;
                var previous = _activeFrameRate;
                _activeFrameRate = ReducedFrameRate;
                AppLog.Info($"Encoder tuning: sustained overload at {_proposedPreset} with no faster preset left - " +
                            $"lowering target frame rate {previous} -> {ReducedFrameRate} fps. " +
                            $"{overloadCount}/{WindowSize} windows severely overloaded, dropped={health.DroppedFrames}, " +
                            $"queue={health.QueueDepth}/{health.EncodeQueueCapacity}, outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}, " +
                            $"adapter={health.AdapterDescription}.");
                FrameRateChangeRequested?.Invoke(this, new EncoderFrameRateChange(previous, ReducedFrameRate));
                _lastDecisionUtc = now;
                _recentOverloads.Clear();
                _cleanSinceUtc = null;
                _queueDepthSinceClean = 0;
                return;
            }

            AppLog.Info($"Encoder tuning: sustained overload at {_proposedPreset}, already at the fastest preset and " +
                        $"{_activeFrameRate} fps - {overloadCount}/{WindowSize} windows severely overloaded, dropped={health.DroppedFrames}, " +
                        $"queue={health.QueueDepth}/{health.EncodeQueueCapacity}, outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}. " +
                        "Capture resolution is the remaining lever, and it needs a settings change.");
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
                    $"{overloadCount}/{WindowSize} windows severely overloaded, dropped={health.DroppedFrames}, " +
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

public sealed record EncoderFrameRateChange(int PreviousFrameRate, int FrameRate);
