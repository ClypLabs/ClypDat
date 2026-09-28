# Native recording

The Windows replay and full-session recorder runs in `RecorderCore`, inside
`ClypDat.Capture.Native.dll`, which `ClypDatRecorder.exe` (the capture worker)
loads. There is no managed capture or encoding path and no backend choice.

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
also exercised by live replay use. AMF and QSV (zero-copy and readback) are
implemented and covered by the encoder plan and failover tests. Real AMD H.264
validation passed both focused encoder inputs and full-app D3D11 zero-copy replay;
see the [AMD validation results](../../docs/replay-keyframe-validation.md) for
hardware, measurements, and limits. Full-app AMF readback and real Intel hardware
remain unverified; AV1 AMF initialization is a separate unresolved issue.
