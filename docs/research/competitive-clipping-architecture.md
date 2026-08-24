# Competitive clipping architecture: public evidence

> Scope: public vendor documentation/support material, checked 2026-08-24.
> This is not binary/protocol reverse engineering. Undocumented vendor internals
> are recorded as unknown, not inferred from behaviour.

## Takeaways

Discord, Medal, and SteelSeries all provide local retrospective clips. Medal
and SteelSeries explicitly document hardware encoding; Discord exposes a Clips
hardware-encoding setting but does not identify its capture or media pipeline.
None publicly specifies capture API, encoder settings, GOP policy, A/V clock,
or rolling-buffer implementation. Their products demonstrate desired behaviour,
not implementation details to copy.

## Discord Clips

### First-party facts

- Clips saves a recent 30-second, 60-second, or two-minute window with `Alt+C`;
  clips are local and storage is configurable. [Discord: Clips](https://support.discord.com/hc/en-us/articles/16861982215703-Clips)
- Application streaming, rather than screen streaming, is required for game
  audio. Clip editor controls game audio and voice-channel audio separately.
  [Discord: Clips](https://support.discord.com/hc/en-us/articles/16861982215703-Clips)
- Hardware Encoding is an optional Clips setting. Discord says it compresses
  videos, identifies Windows/GPU eligibility, and warns of game-performance
  impact on lower-end systems. It does not identify a capture API, codec,
  bitrate, FPS, resolution, container, or buffering design.
  [Discord: Clips](https://support.discord.com/hc/en-us/articles/16861982215703-Clips)
- Discord documents OS codecs, AVC, and AV1 elsewhere, but not as a Clips
  specification. [Discord: Microsoft Store Codecs](https://support.discord.com/hc/en-us/articles/37976724130711-Microsoft-Store-Codecs-for-Discord)

### Unknown

Public documentation does not say whether Clips uses WGC, DXGI duplication,
game hooks, or another source, nor whether it retains encoded packets, frames,
or temporary media segments.

## Medal.tv

### First-party facts

- Clip Capture saves preceding 15/30/45/60/120-second clips, with options up to
  20 minutes, and stores only selected clip length. It detects game launch/exit.
  [Medal: recording methods](https://support.medal.tv/support/solutions/articles/48001157616-how-to-choose-your-recording-method), [Medal: record and make clips](https://support.medal.tv/support/solutions/articles/48001157618-how-to-record-and-make-clips)
- Long Recording is separate: it records launch-to-exit; clip hotkeys become
  bookmarks; storage grows with session length.
  [Medal: record and make clips](https://support.medal.tv/support/solutions/articles/48001157618-how-to-record-and-make-clips)
- Medal exposes GPU/CPU encoder selection, recommends GPU for quality, and
  recommends considering CPU when game GPU exhaustion makes clips choppy.
  [Medal: choppy clips](https://support.medal.tv/support/solutions/articles/48000922094-low-framerate-fps-lag-or-choppy-clips)
- Quality guidance covers H.264, H.265, AV1, bitrates by resolution, and warns
  that higher resolution/bitrate increases resource use.
  [Medal: recommended quality](https://support.medal.tv/support/solutions/articles/48001159800-medal-recommended-clip-quality-settings)
- Choppy/lost-clip remediation includes lowering quality, encoder change,
  CPU/GPU pressure, game FPS/VSync, recorder conflicts, drivers, and logs.
  [Medal: choppy clips](https://support.medal.tv/support/solutions/articles/48000922094-low-framerate-fps-lag-or-choppy-clips)
- For corrupt/missing clips, Medal suggests local-file inspection, borderless or
  windowed mode, and Advanced Window Capture.
  [Medal: missing or corrupted clips](https://support.medal.tv/support/solutions/articles/48000965398-my-clips-are-missing-or-corrupted-)

### Unknown

Medal does not specify Advanced Window Capture internals, capture API,
timestamping, audio resampling, GOP interval, or rolling-media representation.

## SteelSeries GG Moments

### First-party facts

- Moments has hotkey retrospective clips and game-specific auto-clips. Supported
  events become timeline markers for editing/sharing.
  [SteelSeries: Moments](https://steelseries.com/gg/moments)
- SteelSeries says Moments uses GPU video-encoding capacity, varies bitrate with
  frame complexity, and records selected-interval clips to manage disk usage.
  It promotes high-frame-rate recording without publishing a maximum FPS.
  [SteelSeries: Moments](https://steelseries.com/gg/moments)
- Moments supports independent audio sources, non-destructive trims, and export
  as a new file. Its A/V synchronization method is unspecified.
  [SteelSeries: Moments](https://steelseries.com/gg/moments)
- Troubleshooting calls out HDR, same-drive GG/game, primary-monitor capture,
  recorder conflicts, drivers, detection, and hotkeys.
  [SteelSeries: Having Issues Capturing Clips](https://support.steelseries.com/hc/en-us/articles/360060109772-Having-Issues-Capturing-Clips)

### Unknown

SteelSeries does not name capture API/codec, publish limits, or describe queue,
timeline, timestamps, or clip-save transactions.

## Engineering implications for ClypDat

These recommendations derive from public evidence and ClypDat's reported
high-FPS issue; they are not claims about competitor internals.

1. Record independent capture callback/input FPS, unique processed FPS, and
   encoded/muxed FPS. Include requested/applied WGC interval, drops/duplicates,
   resize events, and capture-route changes. CFR output must not conceal low
   unique source cadence.
2. Keep a bounded, timestamped rolling media buffer anchored to one monotonic
   clock. Test export with `ffprobe`: requested duration, first/last PTS, frame
   cadence, audio continuity, and A/V end-time delta.
3. Separate capture pressure from encoder pressure. Measure capture cadence,
   CPU/GPU encode latency, queue depth/age, encode errors, and mux/write time.
   Offer visible, rate-limited encoder/quality/FPS mitigation instead of silent
   degradation.
4. Keep current WGC policy: request/read back selected interval; only switch to
   DXGI after sustained foreground, non-overloaded under-delivery; log fallback
   reason/privacy implication; reset probe on target, resize, rate, and
   foreground/minimized changes.
5. Add compatibility diagnostics: source/target identity, WGC/DXGI result, HDR,
   elevation mismatch, monitor/adapter, recorder conflicts where practical,
   driver, encoder/codec, and bounded telemetry trace.
6. Build black-box acceptance tests, not competitor-internal assumptions:
   30/60/90/120 FPS; supported codecs; foreground/covered/minimized; resize/HDR;
   long session; overloaded save; audio-source changes. Verify unique input FPS,
   output duration, and A/V sync.

## Safe next investigation

Public sources support telemetry-first rolling-buffer architecture but cannot
reveal proprietary implementation. Next step: deterministic ClypDat video/audio
fixtures plus normal product use; not decompilation, hooks, DRM/anti-cheat
bypassing, or claims based on undocumented internals.
