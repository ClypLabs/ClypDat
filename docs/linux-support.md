# Experimental KDE support

The Linux implementation keeps the shared layouts, navigation, themes, settings
and editor controls. It remains experimental pending the live acceptance below.
The banner now reports actual worker readiness and failure reasons.

## Build and run

Run `./publish-linux.sh` from `clypdat-app`. SDK 10.0.302, matching Avalonia
12.2.1006-linux.2 packages, private GSR and the self-contained app/worker are built
into `.local/linux-x64`. `CLYPDAT_LINUX_OUTPUT` can select a separate verification
folder. The unchanged Windows package pin remains 12.2.1006. The sibling Avalonia
checkout must match `eng/AvaloniaPin.props`.

Native build prerequisites and pinned sources are in `native/gsr/UPSTREAM.md`.
Runtime requires KDE Wayland with window-management v17 and screencast v6,
PipeWire/PipeWire-Pulse, compatible graphics/encoding drivers, LibVLC 3 with its
plugins, libpulse and libpulse-simple, libsqlite3, Secret Service (`secret-tool`),
`notify-send`, `busctl`, `dbus-monitor`, `pactl`, `xdg-desktop-portal-kde`, and
`/usr/bin/ffmpeg` / `/usr/bin/ffprobe`. FFmpeg needs the configured software
encoder for exact trims (libx264, libx265 or libsvtav1). .NET is bundled; system
native libraries are not statically bundled. Distro FFmpeg is used by subprocess.

Stop the previous user service before publishing over its executable folder.
Then register and start the build:

```sh
python3 .local/linux-x64/install-desktop.py
systemctl --user daemon-reload
systemctl --user start clypdat-experimental.service
journalctl --user -u clypdat-experimental.service -b --no-pager
```

The installer registers the exact private recorder path for KDE interfaces and a
persistent user service that unsets `DISPLAY`. Rerun registration after moving
the folder. Existing autostart settings remain authoritative. Credentials use
Secret Service; no plaintext fallback or Windows updater is enabled.

## Capture and saves

KDE metadata enters shared custom/catalog/Steam classification. Native and
Proton identities include process start time to reject reused PIDs. Background
or minimized selected games remain selected. Monitors use real output names;
an unavailable named output never changes to the first monitor.

Worker protocol 13 includes readiness. The private recorder targets screencast
v6 object serials and owns the last foreground GPU texture. An initial isolated
window frame seeds that texture when capture starts with the game in the background. Focus loss/minimize
repeats that frame while audio and timestamps continue. Lock/suspend tears down
capture; source/service failures trigger worker retries of the selected source.

Replay explicitly uses RAM, with an owned mode-0700 Unix socket directory.
Two replay finalizations may overlap. A concurrent native snapshot returns a
busy failure immediately; it is never silently queued for a different interval.
Each completion retains its save ID. Snapshots include timeline metadata and
requested intervals are mapped to that clock. Exact keyframe/packet boundaries
without frame reordering can copy video; other trims re-encode with the chosen
codec. GPU mode uses a probed hardware encoder for conversion and fails with
recoverable staging if unavailable; CPU encoding requires explicit CPU mode.
Preroll remains in staging. Pending output is not a library media file;
only a successful mux/probe publishes it. Failed staging and request metadata
remain under the application data `replay-staging` directory for recovery.

Full sessions toggle the shared encoder without restarting replay. Background
codec conversion is serialized, reports progress and retains the existing quota
and sidecar format. Failed session staging remains recoverable.

Application audio follows PipeWire executable identities, with PID/application
ID support in the native selector. Game mix excludes Chat/additional selections;
microphones remain separate. FFmpeg finalization applies gain and microphone
mono/stereo processing while copying video whenever timing/codec allow it.

KDE shortcuts are worker-owned stable actions `save-replay` and
`toggle-full-session`. Existing shortcut controls open KDE configuration through
a leased Wayland parent and show confirmed trigger descriptions. Initial binding
can require KDE confirmation. Button saves remain available without bindings.

## Editor

Shared video/audio interfaces retain the existing mixer, seek/speed logic and
composition model. Linux video uses LibVLC callbacks and three leased buffers,
rendered by Avalonia/Skia. Text/blur selection and playback controls are hosted
inside the Linux tree, including fullscreen. Linux audio runs PulseAudio writes
on a dedicated thread and exposes measured output latency to the shared clock.

Verification on 2026-09-14: 37 Linux app tests and 41 Avalonia Wayland tests
passed. All six native fixture suites passed. The 720p software Skia fixture
decoded at 30 FPS for three seconds, averaging approximately 1 ms per draw.
Linux uses one software decoder thread: the host's LibVLC 3/FFmpeg frame-thread
pool stalled after seeking with the shared Windows thread count. A fresh pinned
recorder checkout built with the shipped source procedure. Windows managed
app/test assemblies cross-compiled successfully. A native Wayland backend check
with DISPLAY unset captured 1080p60, finalized a replay and concurrent session,
and kept replay running after session stop. A captured 1080p60 gameplay clip
decoded at 60 FPS in the eight-second Skia playback check (1.5 ms per draw). Gameplay image/audio acceptance
and lock/suspend recovery still require user checks.

## Validation and acceptance

```sh
./eng/dotnet-linux.sh test native/tests/ClypDat.Linux.Tests/ClypDat.Linux.Tests.csproj -p:ClypDatLinux=true -c Release
python3 native/gsr/tests/test-watcher.py native/gsr/build/source/build/gpu-screen-recorder
python3 native/gsr/tests/test-kde-targets.py
python3 native/gsr/tests/test-replay-ram.py
python3 native/gsr/tests/test-mux-failure.py
python3 native/gsr/tests/test-ipc-contract.py
python3 native/gsr/tests/test-parent-lifetime.py
# In clypdat-avalonia:
../clypdat-app/eng/dotnet-linux.sh test --project tests/Avalonia.Wayland.UnitTests/Avalonia.Wayland.UnitTests.csproj -c Release -p:AvsSkipBuildingLegacyTargetFrameworks=True
```

Fixtures cover native/Proton classification, PID reuse, exact source selection,
64-bit serials, focus/minimize and rotated/scaled output metadata, source loss,
RAM snapshot immutability, exact decoded clip frames, copy boundaries, distinct
audio tones/gain/channels, failed muxes and LibVLC frame leasing. RAM fixture:
20,000 packets, two overlapping 1,024-packet snapshots, zero media write bytes,
peak process RSS approximately 143 MiB on this host. This measures the packet
ring fixture, not total live recorder/GPU memory.

Windows managed app/test assemblies can cross-compile on Linux with
`-r win-x64 -p:Platform=x64 -p:EnableWindowsTargeting=true -p:ClypDatManagedValidation=true`.
The feature-branch `validate-linux-branch-windows.yml` workflow builds native
capture/video components and runs Windows tests without release publishing.
Cross-compilation alone does not establish Windows native/runtime acceptance.

Still required for milestone acceptance: actual native/Proton gameplay detection,
replay save and concurrent full session; decoded foreground-frame repetition
through alt-tab/minimize; live PipeWire routing and device/application reconnects;
lock/suspend/service recovery; crop/text/blur/fullscreen and trim/export review;
sustained audible A/V synchronization and playback performance on target GPU;
real recorder memory and replay-only filesystem-write observation. User performs
gameplay checks; logs and saved media establish acceptance. Synthetic fixtures
and startup health alone do not establish this acceptance.

Deferred with runtime guards/status: camera/input overlays, CV and remaining
highlight integrations, microphone noise processing, device-only chat isolation,
HDR, other desktops/GPUs, and release/update packaging.
