# Replay keyframe validation

Branch: `cpp-rewrite`. Real AMD H.264 validation is required before closing the incident.

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

The incident proves the requested automatic cadence was insufficient on that AMD
configuration. It does not establish whether the driver ignored the property or
whether the failure affects system-memory input too. Local hardware is NVIDIA;
AMF cannot initialize without its runtime. Option metadata accepts the AMF settings,
but driver acceptance and output must be checked on AMD.

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

## Real AMD replay acceptance

1. Install the candidate build. Set H.264, Automatic encoder, 1440p, CFR and 120 FPS,
   matching the incident. Set replay length to 120 seconds. Keep full-session recording off.
2. Play for at least seven minutes. Save after minute six, then save again after
   another two minutes. Repeat at 30/60/90 FPS. Keep the diagnostics bundle.
3. Confirm diagnostics report `h264_amf`, the input path, periodic requests and
   emitted keyframes. `keyframeRecoveries` should stay zero. Repeated restart or
   explicit keyframe failure fails acceptance.
4. Inspect both clips with the validation script supplied with this change. Require
   duration at most 121.1 seconds, first packet key, regular key packets throughout,
   valid H.264 IDR payloads, no decode errors, aligned stream starts/durations and
   fast seeking near the end. Listen to all audio tracks at the start and end;
   matching timestamps alone cannot prove perceptual alignment.
5. Repeat the generated encoder comparison for system-memory input. AV1 AMF is
   additional coverage; H.264 AMF is mandatory. The same build must pass the
   generated NVENC tests on NVIDIA.

Do not interpret a successful NVIDIA run as AMD validation. Keep the original
broken file unchanged; it cannot gain keyframes from an application update.
