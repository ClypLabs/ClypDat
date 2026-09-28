# Replay keyframe validation

Branch: `cpp-rewrite`. **The original H.264 AMF replay-keyframe incident is safe
to close on the tested AMD configuration.** Real AMD evidence confirms periodic
IDRs, healthy long-session D3D11 zero-copy capture, bounded saves, normal seeking,
successful decoding, and effective per-game replay duration. No further production
change was required by this review.

Validated candidate: `37acd11292b768282c279ca18f85f48eb2ba3ddd`, including
`ccbee153` (periodic requests and output verification), `6582c059` (safe replay
admission), and `37acd112` (effective replay duration). AMD testing occurred on
2026-09-28. Local review used the same source on the user's NVIDIA machine.

## Evidence scope and provenance

The copied evidence is under `D:\Arashii\Desktop\Log Dump`, treated as read-only.
Paths below are relative to its `validation` directory. The review recalculated
encoder matrices from packet CSVs and clip cadence/timestamps from `probe.json`,
and inspected application/worker logs, health records, settings, and the
validation script. `scope.json` correctly states that this copy contains no
media clips or application binaries. Decode, IDR-payload, and seek results are
recorded AMD measurements, not tests rerun against the media during this review.
All 96 copied files retained identical SHA-256 hashes, sizes, and modification
times after inspection.

`tools-build-manifest.json`, `HANDOFF.md`, and the worker revision identify the
candidate. The prepared archives were unavailable on AMD; the app and tools were
rebuilt from that exact source without production changes. The isolated Dev app
reports `0.0.0+37acd112` and bundles FFmpeg 8.1.2. Supplemental 1080p probes used
the candidate RecorderCore; the original fixture independently passed too.
Copied hashes report that the normal installation and settings were restored
unchanged. The binaries are absent here, so their hashes cannot be rechecked.

User-supplied report ID: `2416b90e-9901-45b8-a454-730498973fe5`. That ID is not
present in the copied files and was not used as verification evidence.

The historical report withheld closure because perceptual A/V sync was unchecked.
This review follows the incident-specific acceptance criteria: that missing
manual check, untested full-app readback, and separate AV1 initialization failure
do not block closing this H.264 zero-copy incident. They remain explicit limits.

## AMD environment

| GPU | Driver | Windows | HAGS |
|---|---|---|---|
| AMD Radeon RX 9070 XT | 32.0.31019.2002; dated 2026-05-29 | Windows Server 2025 Datacenter, build 26100.32860 | On, recorded from dxdiag in `hardware.json` |

The raw dxdiag file is not included. The generic diagnostic OS field says
`10.0.26100`; the more specific Server edition/build comes from `hardware.json`.

H.264 settings: `h264_amf`, NV12, CFR, `gop_size=FPS`,
`usage=ultralowlatency`, `quality=speed`, `rc=cbr`, `forced_idr=1`, `bf=0`,
`preanalysis=0`. Focused logs read back `forced_idr=true`, with successful option
reads and no unsupported options; construction requires its setter to succeed.
`async_depth` was 3/4/6/8 at 30/60/90/120 FPS on both input paths.

The original focused fixture used 320x192 at 5 Mbps; supplemental tests used
1920x1080 at 20 Mbps. The direct adapter run used 1080p60 at 20 Mbps. Beat Saber
used a 1440p cap at 120 FPS, with 20 Mbps reported by the AMD validation report.
Its client was 2560x1440; actual saved video was **2506x1440**, H.264 Main.
Its measured video stream average was 2,909,616 bit/s; this is distinct from the
configured target. A serialized effective game bitrate configuration is absent.
The stored profile's 1080p60/15 Mbps fields were inactive because only its Replay
Length group was enabled, not its Recording Quality group.

## Focused H.264 results

Each explicit run encoded four seconds. Both fixture sizes passed this matrix:

| Path | FPS | Requested keys | Emitted key packets | Request/output cadence | Maximum gap | Result |
|---|---:|---:|---:|---|---:|---|
| D3D11 | 30 | 4 | 4 / 120 packets | 1.000 s / 1.000 s | 1.000 s | PASS |
| D3D11 | 60 | 4 | 4 / 240 packets | 1.000 s / 1.000 s | 1.000 s | PASS |
| D3D11 | 90 | 4 | 4 / 360 packets | 1.000 s / 1.000 s | 1.000 s | PASS |
| D3D11 | 120 | 4 | 4 / 480 packets | 1.000 s / 1.000 s | 1.000 s | PASS |
| System memory | 30 | 4 | 4 / 120 packets | 1.000 s / 1.000 s | 1.000 s | PASS |
| System memory | 60 | 4 | 4 / 240 packets | 1.000 s / 1.000 s | 1.000 s | PASS |
| System memory | 90 | 4 | 4 / 360 packets | 1.000 s / 1.000 s | 1.000 s | PASS |
| System memory | 120 | 4 | 4 / 480 packets | 1.000 s / 1.000 s | 1.000 s | PASS |

Every explicit CSV has requests and `AV_PKT_FLAG_KEY` output at 0/1/2/3 seconds.
First output is key; PTS and DTS are equal and strictly increasing. No path
difference or rejected H.264 option appears. The focused traces record flags,
not NAL payloads; independent payload verification is recorded for live clips.

Automatic-only controls kept `gop_size=FPS` and `forced_idr=1`, but requested only
the startup I picture. Both 1080p input paths emitted **one key packet** at every
FPS, leaving 3.966667/3.983333/3.988889/3.991667 seconds since the last key at
30/60/90/120 FPS. These are failing control results, not candidate failures.
The original smaller fixture stops at its first failing 30 FPS control; the
supplemental fixture continues through all rates. See `amd*.csv`, `amd*.log`,
and `encoder-summary.csv`.

This reproduces ineffective automatic GOP scheduling on both encoder input
paths with this driver/FFmpeg configuration. Explicit `AV_PICTURE_TYPE_I` requests
with `forced_idr=1` fix the observed cadence. It does not isolate responsibility
to the AMD driver or FFmpeg, establish the original incident's exact driver,
or prove behavior across other driver versions.

## Real application and saved replays

| Run | Session before save | Encoder/input | FPS | Requested/emitted keys | Max gap | Recoveries | History peak |
|---|---:|---|---:|---:|---:|---:|---:|
| Direct native adapter | 365.259648 s | H.264 AMF / WGC D3D11 | 60 | 366 / 366 | 1.000 s | 0 | 120.983333 s |
| Beat Saber, full app | 772.439214 s | H.264 AMF / WGC D3D11 | 120 | 773 / 773 | 1.000 s | 0 | 120.991667 s |
| Beat Saber, override removed | 80.100977 s | H.264 AMF / WGC D3D11 | 120 | 81 / 81 | 1.000 s | 0 | 60.991667 s |

Counters above are pre-save snapshots. The long game run began at
01:40:20.1648134 UTC and requested its save at 01:53:12.6040271 UTC. The worker
log has no intervening capture stop/start. Across 637 debug samples during that
interval, `zeroCopy=confirmed`, `h264_amf`, keyframe safety and history safety
remain consistent; keyframe recoveries, history invalidations, readback and GPU
fallbacks are zero. Samples are at most 1.501 seconds apart. The final snapshot
is Healthy, with 772 periodic requests, a **2.100-second watchdog threshold**,
120.000000 seconds retained, and no worker crash loop or recovery attempt.
Later snapshots continue healthy beyond the save. The direct adapter run's
366 health samples also all report Healthy.

One startup reconfiguration completed before the timed game interval. The later
reconfiguration applies override removal. Neither is a watchdog recovery.
The diagnostic controller deliberately closed the isolated UI, reattached to
the existing worker, and issued the production save command without a duration
argument. The UI's subsequent unclean-exit log is explained by this handoff;
capture continued. These runs exercise the actual recorder and app settings
lifecycle, not the save hotkey or settings controls. The report identifies the
retained worker as PID 45336; the worker log independently shows one revision
startup and the corresponding capture transitions.

| Clip | Requested | Actual video duration | Video packets/frames | Key packets / verified IDRs | Full video decode |
|---|---:|---:|---:|---:|---|
| Beat Saber override | 120 s | 120.483333 s | 14,458 | 121 / 121 | PASS; 15.141910 s |
| Beat Saber global fallback | 60 s | 60.141666 s | 7,217 | 61 / 61 | PASS; 8.828072 s |
| Direct adapter | 120 s | 120.316667 s | 7,219 | 121 / 121 | PASS; 7.742593 s |

Every first video packet has `K__`; every clip's minimum/median/maximum keyframe
gap is **1.000/1.000/1.000 seconds**. Recalculated PTS/DTS are equal and strictly
increasing. The long game clip's last key is at 120 seconds, leaving a
0.483333-second tail. `decoded-keyframes.csv` independently lists 121 I keyframes
from 0 through 120 seconds. The recorded `Measure-Clip.ps1` execution verifies
NAL type 5 in every flagged H.264 packet and decodes with errors fatal; its
summaries and empty stderr files record success. No multi-minute expansion
occurred after the 12m52s session.

Sources: `game-validation/session-lengths.json`, `logs/capture-worker.log`,
`logs/clypdat-debug-2026-09-28.log` under `game-validation`, both replay folders'
`health-before-save.json`, `health.jsonl`, `save-result.json`, and
`analysis/{probe.json,clip-analysis.json,keyframes.csv}`. Frame count, dimensions
and track names are independently available in `replay120/analysis/streams.json`.

## Seek performance

Measurements include process startup, input seeking, and one second of video
decode with four CPU threads. They are not a controlled before/after benchmark.

| Clip | Timestamp | Seek plus decode | Result |
|---|---:|---:|---|
| Beat Saber 120 s | 60 s | 0.645743 s | PASS |
| Beat Saber 120 s | 100 s | 0.665624 s | PASS |
| Beat Saber 120 s | 110 s | 0.641879 s | PASS |
| Beat Saber 120 s | 115.483 s | 0.685820 s | PASS |
| Beat Saber 60 s | 55.142 s | 0.693590 s | PASS |
| Direct adapter 120 s | 60 s | 0.585368 s | PASS |
| Direct adapter 120 s | 100 s | 0.580021 s | PASS |
| Direct adapter 120 s | 110 s | 0.579186 s | PASS |
| Direct adapter 120 s | 115.317 s | 0.590063 s | PASS |

Periodic IDRs and subsecond near-end seeks show that the startup-only-keyframe
seek pathology is absent. The original reported >40-second seek was around five
minutes in a different file; no numerical speedup ratio is claimed.

## Objective audio validation

| Beat Saber 120 s track | Duration | Decode | Start delta | End delta versus video |
|---|---:|---|---:|---:|
| All Tracks, AAC | 120.483000 s | PASS | 0 | -0.000333 s |
| Game Audio, AAC | 120.483000 s | PASS | 0 | -0.000333 s |
| Discord, AAC | 120.483000 s | PASS | 0 | -0.000333 s |
| Microphone, AAC | 120.483000 s | PASS | 0 | -0.000333 s |

The fallback clip's four tracks each last 60.141000 seconds, decode successfully,
start at zero, and end 0.000666 seconds before video. The direct adapter clip's
three tracks also decode, start aligned, and end 0.000667 seconds before video.
Four tracks reflect this game's configured sources, not a universal requirement.

Timestamp/decode checks passed. **Perceptual A/V sync was not manually watched
or listened to.** These checks do not prove audible content on every source or
perceptual synchronization; this is a coverage limit, not an observed code failure.

## Per-game replay duration

| Global | Beat Saber Replay group | Worker effective duration | Saved duration | Result |
|---|---|---:|---:|---|
| 60 s | Enabled, 120 s | 120 s | 120.483333 s | PASS |
| 60 s | Disabled; stored value still 120 s | 60 s | 60.141666 s | PASS |

`settings-before-removal.json` and `settings-after-removal.json` retain the same
stored profile duration but remove `Replay` from `Groups`. Logs confirm game
`steam-620980` applied the Replay group initially. After restarting the UI and
reattaching to the worker, capture reconfigured at 01:53:44.9905797 UTC. Effective
duration is demonstrated by 120/60-second retention and saves without a duration
argument; a serialized effective game config is not included. No stale override
remained in the exercised restart/reattachment lifecycle. The settings UI was
not operated. Targeted managed duration tests on AMD passed 7/7.

## Commit review and failure safety

- `ccbee153`: vendor-neutral scheduling uses output PTS and advances only on
  accepted submissions; reused frames clear old I requests. Actual packet flags
  drive health. Encoder-specific option names stay in the encoder adapter.
  The real AMD output and local fixtures support this design; healthy AMD capture
  shows no false watchdog firing or restart loop.
- `6582c059`: history invalidates a broken cadence, rejects inter pictures until
  a verified key, and limits save lead-in and excess duration to 1.1 seconds.
  Observed retention remains within one GOP of the requested window. AMD fixture
  logs confirm unsafe saves are rejected; a two-hour broken-cadence simulation
  retains at most three seconds and ends with zero packets.
- `37acd112`: recorder configuration uses the resolver's effective duration.
  Live override/fallback saves and managed configuration tests support the fix.

No concrete scheduler, history, duration, or vendor-isolation defect was found.
Native fault injection detected missing output keys, reopened once, and failed
explicitly if the replacement remained broken. Existing worker fatal-health
handling also permits only one health-triggered restart. This was automated
failure coverage, not deliberate suppression of AMF keys in the real game.

## Remaining separate work

| Issue or coverage limit | Relation to this H.264 incident | Blocks closing this fix? |
|---|---|---|
| AV1 AMF initialization fails | Separate encoder initialization issue | No |
| Full-app AMF GPU readback to system memory untested | Focused CPU-input encoder passed; complete alternative capture path unverified | No; original D3D11 path passed |
| Perceptual A/V sync unchecked | Objective timing and decoding passed | No |
| Other AMD drivers / exact original driver cause | One tested GPU/driver pair | No; do not generalize coverage |

All four AV1 attempts (D3D11/system memory, 320x192/1920x1080) failed at startup:

```text
encoder->Init() failed with error 1
Open recording encoder: Internal bug, should not have happened
```

Candidate AV1 configuration uses `usage=ultralowlatency`, `quality=speed`,
`rc=hqcbr`, `forced_idr=1`, and planned async depth with `bf=0`, `preanalysis=0`.
The failure occurred in `avcodec_open2` before any periodic request could run;
it is not an explicit-IDR option rejection. Those AMF option values predate the
three fixes, making a pre-existing initialization limitation plausible, but no
pre-candidate AMD AV1 run is included to prove that. H.264 initialized and passed
on the same machine. AV1 needs its own investigation; GPU codec support and the
failing property cannot be inferred from this generic error.

## Local regression review

The 2026-09-28 regression run used an NVIDIA GeForce RTX 4070 Ti, driver
32.0.16.1714. These local checks do not repeat AMD hardware validation.

| Suite | Result |
|---|---|
| Native standard, `./eng/Build-NativeCapture.ps1 -Test` | 17/17 PASS |
| Selected native NVIDIA GPU tests | 7/7 PASS |
| Managed, `./dotnet.ps1 test native/tests/ClypDat.App.Tests/ClypDat.App.Tests.csproj -c Release -p:Platform=x64` | 890/890 PASS, zero skipped |

GPU tests were enabled through CMake, then selected with CTest's filter
`KeyframeNvenc|KeyframeAv1Nvenc|NvencD3D11|GeneratedGpuCapture|GpuOverlayPipeline|GpuDetectorStage`.
The physical-desktop `GpuDxgiCapture` test was excluded; the selected tests use
generated pixels. H.264 NVENC passed D3D11 and system-memory input at
30/60/90/120 FPS. AV1 NVENC D3D11 also passed those rates. Each focused run emitted
four keys in four seconds with a 1.000-second maximum gap.

Local logs, managed TRX results, and independently recalculated AMD measurements
are under the ignored build folder
`native/capture-native/build/amd-evidence-review-20260928/`.

## Evidence and FFmpeg mapping

The original Beat Saber clip contains 43,676 frames and lasts 364.575001 seconds.
Its H.264 payload contains one IDR, at zero, and 43,675 non-IDR pictures. Packet
timestamps are monotonic. This is missing random-access points, not time stretching.

The build pins FFmpeg 8.1.2 (see `eng/Prepare-CaptureFfmpegSdk.ps1`). Its
[AMF H.264 initialization](https://github.com/FFmpeg/FFmpeg/blob/n8.1.2/libavcodec/amfenc_h264.c)
sets `AMF_VIDEO_ENCODER_IDR_PERIOD` from `AVCodecContext.gop_size` after encoder
initialization. Its [submission code](https://github.com/FFmpeg/FFmpeg/blob/n8.1.2/libavcodec/amfenc.c)
maps an explicit I picture with `forced_idr=1` to the AMF IDR picture type and
requests SPS/PPS. The option itself does not schedule I pictures. AV1 uses an
explicit key-picture request and sequence header instead.

The AMD automatic-only controls now reproduce ineffective GOP scheduling on both
input paths, while explicit requests produce periodic key output. The driver
versus FFmpeg attribution remains unresolved.

## Focused encoder comparison

Build with `./eng/Build-NativeCapture.ps1 -Test`. The test program is
`native/capture-native/build/Release/ClypDat.Capture.Native.KeyframeTests.exe`.
To run elsewhere, copy that executable and its FFmpeg DLLs from the same Release
directory into one folder. It generates pixels; it never captures a desktop.

Run these in that folder on AMD. Each command covers 30, 60, 90 and 120 FPS for
four seconds of media. Keep stdout, stderr and the CSV files.

```powershell
./ClypDat.Capture.Native.KeyframeTests.exe --backend h264_amf --d3d11 --auto --trace amd-d3d11-auto.csv
./ClypDat.Capture.Native.KeyframeTests.exe --backend h264_amf --d3d11 --trace amd-d3d11-explicit.csv
./ClypDat.Capture.Native.KeyframeTests.exe --backend h264_amf --auto --trace amd-system-auto.csv
./ClypDat.Capture.Native.KeyframeTests.exe --backend h264_amf --trace amd-system-explicit.csv
./ClypDat.Capture.Native.KeyframeTests.exe --backend av1_amf --d3d11 --trace amd-av1-explicit.csv
```

Nonzero exit means failure or unavailable hardware, never a passing skip. The CSV
records input PTS and explicit I requests, output PTS/DTS, key flags, gaps and input
path. The system-memory test exercises the encoder input used after readback; the
full recorder's readback pipeline has separate generated capture tests.
Use `h264_nvenc` / `av1_nvenc` for NVIDIA, or `h264_qsv` for Intel system input.
Default backend is libx264. The recorder policy and recovery tests run with
`--recorder`, including a backend that ignores automatic GOP insertion.

## Procedure for future AMD replay validation

1. Install the candidate build. Set H.264, Automatic encoder, 1440p, CFR and 120 FPS,
   matching the incident. Set replay length to 120 seconds. Keep full-session recording off.
2. Play for at least seven minutes. Save after minute six, then save again after
   another two minutes. Repeat at 30/60/90 FPS. Keep the diagnostics bundle.
3. Confirm diagnostics report `h264_amf`, the input path, periodic requests and
   emitted keyframes. `keyframeRecoveries` should stay zero. Repeated restart or
   explicit keyframe failure fails acceptance.
4. Inspect both clips with `eng/Test-ReplayKeyframes.ps1` (PowerShell 7). Require
   duration at most 121.1 seconds, first packet key, regular key packets throughout,
   valid H.264 IDR payloads, no decode errors, aligned stream starts/durations and
   fast seeking near the end. Listen to all audio tracks at the start and end;
   matching timestamps alone cannot prove perceptual alignment.
5. Repeat the generated encoder comparison for system-memory input. AV1 AMF is
   additional coverage; H.264 AMF is mandatory. The same build must pass the
   generated NVENC tests on NVIDIA.

Do not interpret a successful NVIDIA run as AMD validation. Keep the original
broken file unchanged; it cannot gain keyframes from an application update.

For a copied script and the installed app's FFmpeg folder:

```powershell
pwsh -File ./Test-ReplayKeyframes.ps1 -Clip 'C:\Clips\new-amd-clip.mp4' -ReplaySeconds 120 -FfmpegDirectory "$env:LOCALAPPDATA\Programs\ClypDat\ffmpeg"
```

The script checks packet timestamps, real IDRs, duration and audio stream timing,
then measures first/near-end decode and decodes every stream with errors fatal.
It writes no media output. `-SkipDecode` performs only packet/timing validation.
