# Editor D3D11 composition

The editor selects `clypdat_d3d11`. VLC converts each decoded picture into an
original-size GPU texture, composes artwork, Gaussian blur and text, then presents
once. Crop-guide subpictures follow composition. Avalonia keeps selection and
pointer handling. Library hover playback and export filters use their existing paths.

The bridge and VLC load the same DLL from `libvlc/win-x64/plugins/video_output`.
ABI 1 contexts carry immutable revisions and seek generations. Artwork uploads
become visible with the next submitted scene. Revision zero blocks presentation
while transport seeks. A pending decoded texture is separate from the last
presented texture, so paused edits cannot expose a future prepared picture.
Scheduled VLC presentation timestamps sample the existing editor clock anchor;
they are not treated as original source PTS. No video snapshot, secondary blur
decoder or fixed-rate video conversion is involved.

Native failures report through the context status. The editor pauses and offers
reopen/driver guidance. Device removal does not fall through to unblurred playback;
reopening creates a fresh device/context. DLL lifetime is process-wide; GPU and
artwork resources belong to each output context.

Build on Windows x64 with Visual Studio 2026 C++ tools, Windows SDK and CMake:

```powershell
./dotnet.ps1 restore native/src/ClypDat.App/ClypDat.App.csproj
./native/video-output-native/build-video-output.ps1
./native/video-output-native/validate.ps1 -Benchmark
```

The app build invokes the native build and publishes its DLL and notices.
`build/` contains generated fixtures, logs and binaries and is ignored by Git.
The exact VLC pin and source modification notice are in `vendor/vlc/UPSTREAM.md`.

Validation uses generated moving bars and unique identifiers read directly from
GPU textures. It covers seven cadence sequences, delayed edits, pending versus
presented pictures, paused redraw, shape masks, artwork ordering, atomic uploads,
obsolete seek generations, destination resizing, injected failure reporting,
reopening after failure and context release. A separate hidden-window VLC
harness exercises hardware/software decode, pause/edit/resume, speed changes,
seeks at 999/1001/1999/2001 ms, output teardown and exclusive source-handle release.
It does not inspect the user's screen or send input.

On the development machine, September 2026, all seven VLC fixtures (24, 29.97,
30, 59.94, 60, 120 fps and VFR) passed. The 120 fps fixture displayed 270 pictures
in a two-second statistics sample, demonstrating no 60 fps conversion cap.
VLC statistics refresh asynchronously, so these counters are not precise frame
pacing measurements. Software decoding also passed.

| Generated fixture | Baseline displayed/lost | Compositor displayed/lost | Compositor dimensions |
| --- | --- | --- | --- |
| 1080p60 | 300/0 | 299/0 | 1920 x 1080 |
| 1440p60 | 315/0 | 300/0 | 2560 x 1440 |
| 4K60 | 315/0 | 300/0 | 3840 x 2160 |

Each benchmark sampled five seconds after two seconds of warm-up, on the same
machine with hardware decode and the editor's no-drop/no-skip options. Reported
additional lost frames were zero percentage points. This is a short regression
check, not a sustained frame-pacing or thermal benchmark. Interactive DPI,
fullscreen and zoom transitions, physical device removal, HDR footage and
end-to-end audible playback still require dedicated acceptance runs.
