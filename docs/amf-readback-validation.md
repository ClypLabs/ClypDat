# AMF system-memory application validation

AV1 and H.264 AMF system-memory input passed real application validation on
2026-09-28 with candidate `b5d936ab` on branch `cpp-rewrite`. The optional AMD
readback validation task can close for the tested hardware/driver combination.
The validation procedure remains below for future hardware runs.

## Real hardware results

| GPU | Driver | Windows | HAGS | Candidate |
|---|---|---|---|---|
| AMD Radeon RX 9070 XT | 32.0.31019.2002 | Windows Server 2025 Datacenter, 26100.32860 | Enabled | `b5d936ab` (`1.6.0+b5d936ab`) |

Evidence: report `cc550b7a-a434-47da-84c9-0351ab3bf62d`, titled
`Full-app AMF readback validation | b5d936ab | RX 9070 XT`, downloaded as
`clypdat-diagnostics-cc550b7a-a434-47da-84c9-0351ab3bf62d.zip`.
All 148 entries in `archive-manifest.json` matched their sizes and SHA-256 hashes
during this review. The AMD package verification separately records 962 candidate
payload files checked without mismatches.

Sources reviewed include `final-report.md`, `validation-method.md`,
`evidence/{environment,candidate-build,package-verification,acceptance}.json`,
both codecs' selector and worker logs, capture/keyframe/history CSVs,
before/after-save health, save results, and `media/` probe, decoded-frame,
keyframe, decode, seek and audio results. Restoration and final-state evidence
record restored normal settings/startup registration, an unchanged installed
executable, stopped candidate processes, and no selector environment variable.
The ZIP contains verification outputs, not replay media; decode and seek results
are recorded AMD runs. This review independently recalculated cadence, packet/
frame counts and timestamp ordering from those outputs, without rerunning media.

### Application path

| Codec | Encoder | hardwareInput | Processing path | Result |
|---|---|---|---|---|
| AV1 | `av1_amf` | `false` | `d3d11-video-processor-readback` | PASS |
| H.264 | `h264_amf` | `false` | `d3d11-video-processor-readback` | PASS |

Both sessions exercised WGC D3D11 capture, GPU conversion, staged D3D11 readback,
reusable system-memory frames, AMD AMF, replay history and replay save in the
real application. Selector logs explicitly confirm `hardwareInput=false`;
capture samples report `zeroCopy=not-used`, nonzero GPU video-processor/readback
timings and bounded staging/CPU pools. `EncoderInputPath=Software` describes
frame storage; the encoders remained hardware AMF. No synthetic encoder harness
was used. After gameplay, a temporary controller disconnected the candidate UI
and used existing IPC to save from the same running worker. The UI hotkey
handler was outside this validation's scope.

### Live health

FPS below is configured FPS followed by median fresh/output FPS. Key counts
are the before-save snapshots; capture continued during saving.

| Codec | FPS: configured; fresh/output | Requested/emitted keys | Max gap | Watchdog recoveries | Encoder recoveries | Pressure drops |
|---|---|---|---|---|---|---|
| AV1 | 120; 120.0/120.0 | 194/194 | 1.000 s | 0 | 0 | 0 |
| H.264 | 120; 119.85/119.9 | 64/64 | 1.000 s | 0 | 0 | 0 |

Observed capture spans, including startup and save completion, were 202.529 s
for AV1 and 66.027 s for H.264. After-save key counts reached 203/203 and 67/67.
Both before/after-save states were Healthy. History peaks were 120.991667 s
and 30.991667 s, within the requested window plus one GOP, with no history
invalidations. Output-frame drops and encoder backpressure drops stayed zero.

AV1 briefly reached 98.0 fresh FPS. That sample still output 120.0 FPS with CFR
duplicates filling missing fresh-source slots; the separate minimum output
sample was 118.9 FPS. No output-frame drop, readback pressure drop or recovery
accompanied the dip. H.264's minimum fresh/output samples were 92.9/119.3 FPS
(also separate samples). These are observed transients, not readback failures.

### Replay result

| Codec | Requested | Actual | Probe codec | FPS | Keyframes | Full decode |
|---|---|---|---|---|---|---|
| AV1 | 120 s | 120.716666 s | `av1` | 120 | 121 | PASS; 14,486 frames |
| H.264 | 30 s | 30.775001 s | `h264` | 120 | 31 | PASS; 3,693 frames |

Both saved files are 1880x1080. Packet and decoded-frame counts match; first
packets and decoded frames are key. Keyframe gap min/median/max is
1.000/1.000/1.000 s for each codec. Video/audio packet PTS and DTS, and decoded
video-frame timestamps, strictly increase. Full video decodes used
`-v error -xerror -err_detect explode`, exited zero and produced empty error logs.
All three seeks per codec passed: midpoint, ten seconds before the end and five
seconds before the end. Timings include process startup and one second of
decode: AV1 1.707-2.011 s; H.264 0.609-0.627 s using the corrected diagnostic
time base described below.

Each replay contains four independently decoded AAC tracks: All Tracks, Game
Audio, Discord and Microphone. All start at video time zero. End deltas versus
video are -0.666 ms for AV1 and -0.001 ms for H.264. These objective checks passed;
subjective listening, visible lip-sync and activity on every source were not
verified.

### Readback

p95 columns show median / maximum of reported gameplay rolling-window p95
samples in milliseconds, not percentiles recomputed over every frame.

| Codec | Staging slots/peak | CPU frames/peak | p95 ms | Map p95 ms | Stalls | Pressure drops | GPU fallbacks |
|---|---|---|---|---|---|---|---|
| AV1 | 2/2 | 2/1 | 0.42/0.59 | 0.03/0.04 | 1, startup only | 0 | 0 |
| H.264 | 2/2 | 2/1 | 0.50/0.61 | 0.03/0.05 | 1, startup only | 0 | 0 |

Each stall counter incremented once before gameplay and remained at one.
Neither run shows sustained readback stalls, CPU-frame exhaustion, growing
frame pools, pressure drops or GPU conversion fallbacks. Watchdog and encoder
recoveries stayed zero. The bounded startup events do not indicate sustained
readback instability.

### Diagnostic caveats

The first AV1 save attempt failed because the temporary controller serialized
`RequestedUtc` as Windows PowerShell's `/Date(...)/`, which the worker rejected.
After the disconnected UI/controller failed to reconnect, the existing ten-second
idle timeout ended that attempt. Only the temporary controller changed, to
`DateTime.UtcNow.ToString("o")`. The entire AV1 run was repeated from the official
launcher and passed. No production code changed; this was neither an AMF failure
nor a watchdog/encoder recovery. The failed attempt remains separately archived
under `evidence/av1-attempt1-helper-error/`.

The initial H.264 midpoint seek logged duplicate-DTS warnings from FFmpeg's
diagnostic null-output muxer, whose output time base was 1/120. The original
`media/summary.json` therefore retains `automated_pass=false`. Source PTS, DTS
and decoded-frame timestamps strictly increase, and full source decode passed.
Repeating all three seeks with `-fps_mode passthrough -enc_time_base demux`
produced zero exits and empty error logs, recorded in `seek-timebase-check.json`
and accepted by `evidence/acceptance.json`. This diagnostic timestamp-rescaling
artifact is not evidence of corrupt replay timestamps and indicates no recorder
timestamp change. A verbose metadata trace also contains
`UDTA parsing failed retrying raw`; track identification and all decodes passed.

### Scope and production decision

The full-app staged-readback path is hardware-validated on **RX 9070 XT + driver
32.0.31019.2002**, on the Windows build above. This does not establish identical
behavior on every AMD architecture, AMD driver version or Windows build.

| Codec | D3D11 zero-copy | System-memory/readback |
|---|---|---|
| AV1 AMF | Validated previously on `2df9aeab` | Validated on `b5d936ab` |
| H.264 AMF | Validated previously on `37acd112` | Validated on `b5d936ab` |

AV1 zero-copy and CBR initialization fix `2df9aeab` are supported by earlier
report `5134d57e-930d-4ee4-a7cb-f4f55a5c3b49` (reviewed final report, health and
media summary). H.264 zero-copy evidence is recorded in
[Replay keyframe validation](replay-keyframe-validation.md). The final readback
package completes the remaining input-path coverage. Periodic replay keyframes
and bounded history/save behavior are validated. **No further production recorder
code change is indicated by AMD testing.**

Keep the system-memory candidate and existing candidate order. D3D11 zero-copy
remains the preferred normal AMD path. The `b5d936ab` selector remains default
OFF, compiled out normally, and validation/test infrastructure only. Successful
hardware validation does not justify enabling it in normal production builds.

## Build gate

Ordinary builds compile the selector out and ignore
`CLYPDAT_DIAGNOSTIC_ENCODER_INPUT`. `Build-NativeCapture.ps1` explicitly
configures `CLYPDAT_ENABLE_ENCODER_INPUT_DIAGNOSTICS=OFF` unless called with
`-EnableEncoderInputDiagnostics`, including after an earlier diagnostic build.
Application publishing forwards that switch only when MSBuild property
`ClypDatEncoderInputDiagnostics=true` is supplied. No AppSettings, worker IPC, or
recorder ABI field enables this capability.

For a validation publish on the development PC:

```powershell
dotnet publish native/src/ClypDat.App/ClypDat.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:ClypDatLocalBuild=true -p:ClypDatEncoderInputDiagnostics=true -o <new-validation-folder>/App
```

With the build gate ON, an unset/empty selector leaves candidate order unchanged.
`amf-system-memory` retains exactly `av1_amf` or `h264_amf`, according to the
requested codec, with system-memory input. A typo, CPU encoder mode, non-AMD
capture adapter, or selected-encoder failure fails explicitly. No alternative
input, codec, vendor, or software candidate remains. Dependency-injected native
test candidates bypass the selector.

## Launch the packaged app on AMD

Extract the ZIP outside the installed app directory. No Visual Studio, source,
Git, CMake, SDK, or compilation is needed. `BUILD.json` records the revision and
diagnostic build gate. `FILES.sha256.csv` records payload hashes.

Exit the existing application and stop its recorder/detector processes before
launching. A running worker would retain its old executable and environment.
The candidate uses the normal ClypDat profile/library; configuration changes
persist. Installed executable files are not replaced.

Run from the extracted package directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start-AmfReadbackValidation.ps1
```

The launcher performs this process-scoped operation (also safe to run directly):

```powershell
if (Get-Process ClypDat,ClypDatRecorder,ClypDatDetectorHost -ErrorAction SilentlyContinue) {
    throw 'Exit ClypDat and stop its recorder/detector before validation.'
}
$start = [Diagnostics.ProcessStartInfo]::new((Resolve-Path '.\App\ClypDat.exe').Path)
$start.UseShellExecute = $false
$start.WorkingDirectory = (Resolve-Path '.\App').Path
$start.EnvironmentVariables['CLYPDAT_DIAGNOSTIC_ENCODER_INPUT'] = 'amf-system-memory'
[Diagnostics.Process]::Start($start)
```

This sets the variable only in the new application process and its children,
including the recorder worker. The PowerShell parent, user environment, and
machine environment are unchanged. Never use `setx`. Stop the application and
worker process tree after validation; do not reuse its worker with another build.

## Before gameplay

Select GPU encoding, AV1, WGC, normal capture resolution/bitrate/audio sources,
120 FPS and a 120-second replay. Check per-game overrides as well.

Verify application and worker revision matches `BUILD.json`. Start capture and
inspect the newest native session log while the recorder is still active:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\ClypDat\native-replay-buffer" -Filter encoder-input-diagnostics.log -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 | Get-Content
```

Expected one-shot log, from the actual successful encoder open:

```text
Diagnostic encoder input selector enabled: requested=amf-system-memory codec=AV1 candidate=av1_amf encoder=av1_amf hardwareInput=false
```

The private native log sits beside the existing session work files. Copy it
before stopping/cleaning the session. Configuration errors are recorded there
too. Existing recorder health remains the source of ongoing telemetry.

Confirm before starting the timed game run:

- `encoder=av1_amf`, `hardwareInput=false`, `zeroCopyStatus=not-used`.
- WGC/D3D11 capture remains active; only encoder input uses system memory.
- `processingPath=d3d11-video-processor-readback`.
- `readbackStagingSlots > 0`, `readbackCpuFrames > 0`.
- AMD AMF and requested AV1 remain selected. No H.264/software fallback.
- No GPU conversion fallback. CPU conversion/upload is not this validation path.

Stop and report mismatches. Do not break D3D11 or select CPU encoding to force
this path. The diagnostic selector must choose it directly.

## AV1 run

Play Beat Saber or an equivalent real game for about 180 seconds, using normal
resolution, bitrate, audio sources, WGC and 120 FPS. Save a filled 120-second
replay near the end. Export diagnostics and copy the selector log before exit.

Collect samples after warm-up and near the end:

- Fresh/output FPS, duplicates, drops and GPU conversion fallbacks.
- Encoder/watchdog recoveries; requested/emitted keys; maximum keyframe gap.
- History duration and memory growth; history should level off at its bound.
- Readback staging slots/in-use/peak; reusable CPU frames/in-use/peak.
- Readback p50/p95, map-wait p50/p95, map stalls and pressure drops.
- Encoder submission p95 and completion p95.

Existing health names include `uniqueFps`, `outputFps`, `duplicates`,
`selectionDropped`, `backpressureDrops`, `encoderStallRecoveries`,
`keyframeRecoveries`, `keyframesRequested`, `keyframesEmitted`,
`keyframeMaxGapUs`, `historyRetainedUs`, `readbackStagingSlots`,
`readbackStagingInUse`, `readbackStagingPeak`, `readbackCpuFrames`,
`readbackCpuFramesInUse`, `readbackCpuFramesPeak`, `readbackP50Ms`,
`readbackP95Ms`, `readbackMapWaitP50Ms`, `readbackMapWaitP95Ms`,
`readbackMapStalls`, `readbackPressureDrops`, `gpuConversionFallbacks`,
`submissionP95Ms`, and `completionP95Ms`.

Validate the saved replay with bundled FFmpeg/FFprobe and the package's
`Verify-ReadbackClip.ps1` in PowerShell 7:

```powershell
pwsh -NoProfile -File .\Verify-ReadbackClip.ps1 -Clip 'D:\path\replay.mp4' -Codec av1 -ReplaySeconds 120
```

Require genuine AV1; duration approximately 120 seconds plus at most one GOP;
valid first key packet and decoded first frame; roughly one-second key cadence;
monotonic timestamps; complete video and every expected audio track decode;
audio start/end alignment. Seek at the midpoint, 10 seconds before the end,
and 5 seconds before the end. Listen for alignment near both ends and confirm
the expected number/content of audio tracks; supply `-ExpectedAudioTracks N`
to assert the configured count. Media checks do not replace live
recorder-health checks.

## H.264 quick check

After AV1 passes, stop the candidate and its workers. Launch again with the
same process-scoped command, select H.264 GPU/AMF and a 30-second replay.
Capture 45-60 seconds. Require `h264_amf`, `hardwareInput=false`, the same GPU
readback path, healthy FPS, periodic keyframes, successful replay save and full
decode. No second long campaign is needed.

```powershell
pwsh -NoProfile -File .\Verify-ReadbackClip.ps1 -Clip 'D:\path\h264-replay.mp4' -Codec h264 -ReplaySeconds 30
```

Preserve logs, health samples, replay files and verifier results. Exit all
candidate processes afterward. Reopening the installed app reconciles its
normal startup registration. Local NVIDIA tests validate gating and pipeline
mechanics with test encoders; they cannot establish AMD hardware acceptance.
