# AMF system-memory application validation

This candidate tests the existing GPU conversion and staged-readback pipeline.
It does not indicate that AMD readback has failed. The previous application
validation was blocked because the working D3D11 encoder candidate opened first.

## Build gate

Ordinary builds compile the selector out. `Build-NativeCapture.ps1` explicitly
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
