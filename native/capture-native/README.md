# Native recording migration

Branch: `cpp-rewrite`, based on `d9b84d9c`. The C# recording pipeline remains
selected by `ReplayBufferFactory.CreateLocal()`. This DLL does not record yet.

The current implementation provides ABI v3 negotiation, copied configuration,
D3D11 device lifecycle, pinned FFmpeg development imports, and executable
Release tests. `cd_engine_save_window` still returns `CD_E_UNAVAILABLE`.
Creating a device does not establish a capture route.

Build from the repository root:

```powershell
./eng/Build-NativeCapture.ps1 -Configuration Release -Test
# Optional physical-device lifecycle checks; does not acquire screen images.
./eng/Build-NativeCapture.ps1 -Configuration Release -TestGpu
```

MSBuild uses this entry point. It discovers Visual Studio 2022 or 2026 C++
tools through `vswhere`; set `CLYPDAT_CMAKE` to override CMake discovery.
7-Zip must be installed. The older local `build.ps1` is not the SDK bootstrap
entry point.

SDK setup downloads Gyan's `ffmpeg-8.1.2-full_build-shared.7z`, checks the
pinned publisher SHA-256, extracts development headers and reference DLLs
under ignored `build/sdk`, and compares all five bundled FFmpeg DLLs with
the reference copies. MSVC import libraries are generated from the verified
bundled exports. Runtime files under `native/vendor/ffmpeg` are unchanged.
The archive and extracted development files are not publish inputs.

The C header and managed structures use explicit 8-byte packing. The managed
probe checks version and structure sizes before creating an engine. Save paths
are caller-owned UTF-16 buffers, with capacities measured in code units.
Callers must serialize destruction against outstanding engine calls.

Tests cover C and C++ structure layouts, ABI rejection, copied configuration,
caller buffer bounds, lifecycle state, and real libx264 encode/decode at
30/60/90/120 fps. PCM silence is resampled from 44.1 kHz to 48 kHz. Device tests
run twenty D3D11 start/pause/stop cycles separately from GPU-free CI tests.
These fixtures verify SDK integration, not recording parity or vendor encoding.

Remaining migration work, in implementation order:

1. Complete recording configuration and control ABI: live settings, diagnostics,
   asynchronous save jobs, cancellation, full-session status, detector/preview
   copies, and overlay artwork ownership.
2. Implement free-threaded WGC, DXGI fallback, privacy policies, recovery,
   crop/scale/HDR processing, pacing, bounded queues, and surface ownership.
3. Move encoder qualification, input paths, tuning, generations, and fallback
   into C++, preserving existing H.264/AV1 policies.
4. Move WASAPI endpoints/process loopback, routing, timestamps, resampling,
   gain, device replacement, and noise-suppression subprocess ownership.
5. Move packet history, pinned save snapshots, muxing, full sessions and crash
   recovery, camera capture/segments, and recording overlay composition.
6. Connect all six managed adapter contracts and validate worker protocol,
   metadata/sidecars, detector crops, overlays, failures and performance.
7. Switch the factory only after parity checks, then remove superseded managed
   recording code while retaining editor/export dependencies.

NVENC, AMF and QSV encoding parity, real capture recovery, concurrent saves,
interrupted MP4/MKV sessions and performance comparisons remain unverified.
