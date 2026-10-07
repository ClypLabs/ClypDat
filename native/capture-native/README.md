# Native recording

The Windows replay and full-session recorder runs in `RecorderCore`, inside
`ClypDat.Capture.Native.dll`, which `ClypDatRecorder.exe` (the capture worker)
loads. There is no managed capture or encoding path and no backend choice.

Intel QSV recording supports H.264 only. AV1 QSV is disabled until hardware
that supports AV1 encoding is available for validation. NVIDIA NVENC and AMD AMF
retain H.264 and AV1 support. An AV1 preference can use the existing H.264
fallback candidates, including `h264_qsv`; the selected encoder and saved codec
report H.264. QSV retains both zero-copy and staged-readback inputs, with
low-power candidates tried before the full encoder. Real Intel hardware
acceptance is still pending; passing local plan tests does not establish it.

- Native: capture, pacing, conversion, encoding, replay history, audio,
  save/remux, Full Session, burned overlays and detector frame extraction.
- Managed (`ClypDat.App`): UI and settings, worker IPC (`CaptureWorkerProxy`,
  `CaptureWorkerHost`), the `NativeRecordingAdapter` over the C ABI in
  `clypdat_recorder.h`, clip and Full Session publication, the library, and
  capture health presentation.

The recorder ABI (v3) reports six capabilities; the managed session requires
all six, and the loader verifies structure sizes and the pinned FFmpeg
versions before creating a recorder.

Capture uses free-threaded Windows Graphics Capture with a same-device DXGI
fallback, bounded frame queues, native pacing, aspect-fit, SDR/HDR conversion,
cursor rendering, detector crops and encoder-generation recovery. WASAPI
loopback and microphone sources feed timestamped disk histories. Native save
jobs pin their selected ranges, align named audio tracks, support cancellation,
and keep terminal results until the adapter acknowledges them. Full-session
writers rotate across compatible encoder generations; interrupted MP4/MKV
files recover through bounded, supervised FFmpeg jobs. Camera segments and
input transitions have versioned sidecar publication.

Build and run native tests from the project root:

```powershell
./eng/Build-NativeCapture.ps1 -Configuration Release -Test
./eng/Build-NativeCapture.ps1 -Configuration Release -TestGpu
```

Window recordings crop to the physical client area before pacing, detector
sampling and encoding. WGC matches each frame's content size to DWM's visible
frame or the physical window rect, retaining that crop with borrowed buffers;
DXGI uses the same physical client bounds. Missing or inconsistent geometry is
skipped during resize. The initial recording aspect ratio also uses the client.
`ClypDat.Capture.Native.WindowCaptureTests` checks generated window pixels,
hidden-window DPI queries and decoded video; `--gpu` checks generated NVENC
output without capturing a screen or visible window. See
[window capture validation](../../docs/window-capture-validation.md).

`-TestGpu` uses generated D3D11 textures and NVENC, never the desktop or user
input devices. The native tests cover generated H.264/AV1 at 30, 60, 90 and
120 fps in CFR and VFR, forced NVENC generation replacement, decode and seek,
HDR conversion, detector crops, overlay timing, suppression, shutdown and
recovery.

The auto-clip detector (`DetectorStage`) reads back only the source pixels its
three regions depend on, into one reused staging texture, and converts only the
output rows they cover with the same swscale context and filter as the
full-canvas conversion, so region and mask bytes match it exactly. Two manual
tools check that: `ClypDat.Capture.Native.DetectorCorpus <frames dir> <out.tsv>`
hashes detector output for captured game frames so two builds can be compared,
and `ClypDat.Capture.Native.DetectorBench <seconds> <stage|reference|off|idle>`
measures detector cost on the pipeline with a generated 4K source.

The Desktop Duplication fallback copies each frame into the same bounded pool
of owned textures WGC uses (a frame is dropped and counted when every pooled
texture is still held), polls for frames because a waiting
`AcquireNextFrame` holds the device lock the encoder needs, and composes the
cursor on the GPU (`CursorCompositor`) with DrawIconEx's exact arithmetic;
only cursors it cannot reproduce read their rectangle back to the CPU.
`ClypDat.Capture.Native.DxgiCaptureTests` compares both cursor paths byte for
byte, and `ClypDat.Capture.Native.DxgiCaptureBench` measures the fallback
against its previous per-frame path (`--reference`).

The replay history prunes incrementally from a keyframe index, keeping the
same packets as the original full scan, which the tests run alongside it.
`ClypDat.Capture.Native.VideoHistoryTests --bench` compares their cost, and
`ClypDat.Capture.Native.ReplaySaveCheck <seconds> <work dir> <ffmpeg.exe>
[--reference]` records the primary monitor through a RecorderSession, saves
the whole history and checks the clip decodes, seeks and stays in sync.

WGC frames stay in the capture API's buffers until they are used: the pacer
picks from the same timestamps and queue as before, and only the frame it
picks (or the detector samples) is copied into an owned texture; every other
frame goes back uncopied. The frame pool grows to the queue depth plus four
buffers (`capture_wgc_pool_buffers`) so borrowing never makes WGC skip a
composition. `ClypDat.Capture.Native.WgcCadenceTests` models pool ownership
across 30-120 FPS on 60-360 Hz displays and checks the selected frames,
timestamps and tick mapping are identical to copying every arrival; at
1440p120 on a 240 Hz display in a GPU-bound game this cut owned copies from
about 200/s to 122/s and returned about 10 fps to the game.
`CLYPDAT_WGC_COPY=arrival` restores a copy per arrival, and
`CLYPDAT_CAPTURE_TRACE=<file>` writes every acquisition and output tick for
replay.

WGC's MinUpdateInterval is a whole number of display ticks leaving the
producer 1.5x the active recording rate (`capture_wgc_update_ticks`), reapplied
whenever the rate or refresh changes. `ClypDat.Capture.Native.WgcCadenceTests`
models composition ticks, the interval gate and output selection across
30-120 FPS on 60-360 Hz displays: every coarser cadence keeps fresh-frame counts
but samples a sparser grid, raising output judder or latency, so the policy
stays. `--explore` prints the full matrix, and
`ClypDat.Capture.Native.WgcCadenceBench <fps> <ticks,...> <source fps,...>`
measures a fixed cadence on the real primary display with a 48x48 presenter
window.

The [five-round generated media comparison](RECORDING-COMPARISON.md) reports
CPU time, save time, working set and private bytes for the managed and native
recorders during the migration; the managed recorder has since been removed.
Local fixture clips and per-run JSON remain under `.local/`.

The native tests use generated textures and never physical screen/window
acquisition or real WASAPI device replacement; the RTX 4070 Ti NVENC paths are
also exercised by live replay use. AMF and H.264 QSV (zero-copy and readback) are
implemented and covered by the encoder plan and failover tests. Real AMD H.264
and AV1 validation passed D3D11 zero-copy and full-app staged readback; see
[AMD readback validation](../../docs/amf-readback-validation.md) and
[replay keyframe validation](../../docs/replay-keyframe-validation.md) for
hardware, measurements, and limits. Real Intel H.264 hardware acceptance remains
pending. Run `ClypDat.Capture.Native.CaptureTests --qsv` on an Intel adapter for
the generated zero-copy/readback checks, followed by real application capture,
replay save, keyframe, decode, seek and audio validation.

## Graphics recovery and storage admission

Device loss retains its original HRESULT, device removal reason, adapter name
and LUID in native health JSON. Capture, conversion and encoding stop using
that device. The UI stays armed, says “Waiting for graphics device”, and retries
with a fresh worker immediately, then after 1, 2, 4, 8 and 10 seconds; subsequent
delays remain 10 seconds. GPU outages do not consume the ordinary crash budget.
Control and health requests have a five-second limit and startup has a
45-second limit. Replacement waits for confirmed exit of the preceding worker.

Accepted replay saves may finish from pinned history for up to 30 seconds
before replacement. Each unresolved save receives one interruption result and
is never retried against replacement history. Completed files remain. A fresh
replay buffer and a new, unique Full Session file replace the lost recording;
the current target, audio routing, pause state, hotkeys and overlays are restored.

Save admission checks the requested destination, native working storage under
the app data root, and system temporary storage. Its estimate uses the requested
clip window and bitrate, a 3x allowance, and a 2 GiB reserve. Inactive Full
Session destinations and obsolete configured roots cannot block a replay save.
Total save duration is telemetry; it is not a disk write latency sample.
Capacity and write latency causes have independent recovery state and retain
their explanations throughout cooldown.

`ClypDat.Capture.Native.CaptureTests --graphics-loss` injects loss at capture,
conversion, encoding and a silent frame source, then verifies fresh sessions.
The managed storage/recovery tests also cover five-minute simulated outages,
wire request timeouts, restored configuration and save interruption. These use
generated media and do not restart a physical graphics driver. Final acceptance
requires a user-controlled graphics outage with replay and Full Session armed:
check automatic resumption, unique Full Session output, buffer-reset wording,
completed/interrupted save results, cancellation when recording is switched off,
and a second outage in the same app session.
