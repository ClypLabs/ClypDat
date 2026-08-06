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
// CAPTURE HEIGHT is the last lever, and this acts on it too. Once P1 and 30fps
// are both spent there is nothing left that does not touch resolution, and the
// previous behaviour was to log "capture resolution is the remaining lever, and
// it needs a settings change" and then leave the machine there: a real Iris Xe
// session sat at 1.5-18fps of a requested 30 at 1080p for eight minutes,
// producing clips nobody would want to keep, while the log quietly explained
// what a human could have done about it. Unlike the frame rate this costs a
// buffer restart (the encoder bakes its output size in at start), which is why
// it is last and why it only ever fires once per run - but a restart that loses
// the buffered window is cheap next to minutes of 1.5fps footage.
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

    // Same shape for height: one step, to the lowest preset the settings UI
    // offers. Anything below 720p is a deliberate choice, not a rescue.
    private const int ReducedHeight = 720;
    private const int MinimumHeightToReduce = 1080;

    // Raised when the target frame rate should change. MainWindow wires this to
    // the live buffer and to the user-facing toast.
    public event EventHandler<EncoderFrameRateChange>? FrameRateChangeRequested;

    // Raised when capture height should drop. MainWindow applies it as a
    // session override (the user's own setting is left alone) and restarts the
    // buffer.
    public event EventHandler<EncoderResolutionChange>? ResolutionChangeRequested;

    private int _configuredFrameRate;
    private int _activeFrameRate;
    // One step down per session. Handing the rate back and taking it away again
    // would be worse than either, and this machine has already shown what it
    // can do.
    private bool _frameRateReduced;

    // Height, unlike the frame rate, is NOT reset per session: applying it
    // restarts the buffer, and BeginSession is what a restart calls. Resetting
    // it there would let the tuner drop the height again on the very session
    // its own last drop created, one step per restart, forever.
    private int _activeHeight;
    private bool _heightReduced;

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
    // Restoring the frame rate is held to a much shorter streak than a preset
    // promotion. The asymmetry is deliberate: a preset is the tuner's own
    // judgement call, but the frame rate is the number the user typed into
    // Settings, and running below it is a visible, surprising deviation from
    // what they asked for. Ten minutes of that is far too long to wait,
    // especially since the reduction is reversed the moment it proves
    // unnecessary rather than costing anything to try.
    private static readonly TimeSpan RestoreFrameRateAfterClean = TimeSpan.FromMinutes(2);

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
        _activeFrameRate = configuredFrameRate;
        _frameRateReduced = false;
        _activeHeight = configuredHeight;
        AppLog.Info($"Encoder tuning: observing session at preset {_ceilingPreset}, {configuredFrameRate} fps, {configuredHeight}p (preset changes are observe-only; frame rate and capture height can be lowered if the encoder cannot keep up).");
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
                    (_frameRateReduced ? $" Target frame rate was lowered {_configuredFrameRate} -> {_activeFrameRate} fps." : string.Empty) +
                    (_heightReduced ? $" Capture height was lowered to {_activeHeight}p." : string.Empty));

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
        // `severe` as well as the window count: the window holds 30s of history
        // and the cooldown holds a decision back for 60s, so a burst that ended
        // half a minute ago could still spend a lever while the encoder is
        // visibly fine RIGHT NOW. That is not theoretical - it fired on this
        // line and halved a capture's frame rate:
        //   "sustained overload ... 8/15 windows severely overloaded,
        //    dropped=0, queue=0/30, outputFps=60.0/60"
        // Zero drops, empty queue, output exactly at target. Requiring the
        // current sample to be bad too means a lever is only ever spent on a
        // problem that is still happening.
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
            // Neither of the levers below can help unless the ENCODER is the
            // thing that is behind, and a backed-up encode queue is the only
            // evidence of that. Output falling short of target has other causes
            // entirely - a capture thread stalling, a source that is not
            // presenting - and halving the frame rate or the capture height
            // fixes none of them; it just halves the user's clips.
            //
            // This fired with "dropped=7, queue=0/30, outputFps=32.0/60" while
            // encode was running at 0.6ms a frame: an empty queue, an idle
            // encoder, and a user who asked for 60 getting 30 with no visible
            // explanation. The real shortfall there was ~200ms capture stalls.
            var encoderIsBehind = health.EncodeQueueCapacity > 0 &&
                                  health.QueueDepth * 2 >= health.EncodeQueueCapacity;
            if (!encoderIsBehind)
            {
                AppLog.Info($"Encoder tuning: output short of target ({health.OutputFrameRate:0.0}/{health.TargetFrameRate}) but the encode queue is " +
                            $"{health.QueueDepth}/{health.EncodeQueueCapacity} - the encoder is keeping up, so this is not something a lower " +
                            "frame rate or capture height can fix. Leaving the configured settings alone.");
                _lastDecisionUtc = now;
                _recentOverloads.Clear();
                return;
            }

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

            // Frame rate spent too. Capture height is the last thing left, and
            // it costs a restart - see this class's header.
            if (!_heightReduced && _activeHeight >= MinimumHeightToReduce)
            {
                _heightReduced = true;
                var previousHeight = _activeHeight;
                _activeHeight = ReducedHeight;
                AppLog.Info($"Encoder tuning: sustained overload at {_proposedPreset} and {_activeFrameRate} fps with no faster preset left - " +
                            $"lowering capture height {previousHeight}p -> {ReducedHeight}p. " +
                            $"{overloadCount}/{WindowSize} windows severely overloaded, dropped={health.DroppedFrames}, " +
                            $"queue={health.QueueDepth}/{health.EncodeQueueCapacity}, outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}, " +
                            $"adapter={health.AdapterDescription}.");
                ResolutionChangeRequested?.Invoke(this, new EncoderResolutionChange(previousHeight, ReducedHeight));
                _lastDecisionUtc = now;
                _recentOverloads.Clear();
                _cleanSinceUtc = null;
                _queueDepthSinceClean = 0;
                return;
            }

            AppLog.Info($"Encoder tuning: sustained overload at {_proposedPreset}, already at the fastest preset, " +
                        $"{_activeFrameRate} fps and {_activeHeight}p - {overloadCount}/{WindowSize} windows severely overloaded, dropped={health.DroppedFrames}, " +
                        $"queue={health.QueueDepth}/{health.EncodeQueueCapacity}, outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}. " +
                        "Every automatic lever is spent; this machine needs a lower capture setting than it is configured for.");
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
        if (_cleanSinceUtc is null) return;
        // The user's own frame rate comes back first and on its own, shorter
        // clock - see RestoreFrameRateAfterClean.
        if (now - _cleanSinceUtc < (_frameRateReduced ? RestoreFrameRateAfterClean : PromoteAfterClean)) return;

        // Frame rate comes back before any preset does. It was a one-way latch:
        // nothing anywhere restored it, so a single bad fight pinned the rest of
        // the session at half the configured rate, and every clip saved after it
        // read 30fps on a 60fps setting with no explanation the user could see.
        // The preset ladder below is observe-only, which meant the levers that
        // actually take effect were exactly the ones that could never be undone.
        //
        // Guarded by the same clean streak and queue-headroom test promotion
        // uses, so this only happens when the machine has demonstrably been
        // coping for the full window.
        if (health.EncodeQueueCapacity > 0 && _queueDepthSinceClean * 4 >= health.EncodeQueueCapacity)
        {
            return;
        }

        if (_frameRateReduced && _activeFrameRate < _configuredFrameRate)
        {
            var previous = _activeFrameRate;
            _activeFrameRate = _configuredFrameRate;
            _frameRateReduced = false;
            AppLog.Info($"Encoder tuning: restoring target frame rate {previous} -> {_configuredFrameRate} fps - " +
                        $"clean for {(now - _cleanSinceUtc.Value).TotalMinutes:0.0} min, " +
                        $"peak queue since clean {_queueDepthSinceClean}/{health.EncodeQueueCapacity}, " +
                        $"outputFps={health.OutputFrameRate:0.0}/{health.TargetFrameRate}.");
            FrameRateChangeRequested?.Invoke(this, new EncoderFrameRateChange(previous, _configuredFrameRate));
            _lastDecisionUtc = now;
            _cleanSinceUtc = null;
            _queueDepthSinceClean = 0;
            return;
        }

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

public sealed record EncoderResolutionChange(int PreviousHeight, int Height);
