# Native recording

The Windows replay and full-session recorder runs in `RecorderCore`. The
managed adapter owns settings, worker messages and clip publication. It has no
managed capture or encoding fallback. ABI v3 reports all six required recording
capabilities; the loader verifies structure sizes and pinned FFmpeg versions
before opening the engine.

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

The [five-round generated media comparison](RECORDING-COMPARISON.md) reports
CPU time, save time, working set and private bytes for managed and native
recorders. Local fixture clips and per-run JSON remain under `.local/`.

Physical screen/window acquisition and real WASAPI device replacement were
not exercised during this migration. The RTX 4070 Ti NVENC paths used only
generated textures. AMD and Intel encoder hardware paths remain unverified.
