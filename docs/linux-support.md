# Experimental Linux support

This branch is a **foundation build, not the completed KDE milestone**. Do not
use it for gameplay capture. The app explicitly refuses replay and editor
playback until their Linux backends exist. No Windows installer may be downloaded
or launched through the Linux update path.

## Build and run

Run `./publish-linux.sh` from `clypdat-app`. It bootstraps SDK 10.0.302 into
`$XDG_CACHE_HOME/clypdat/dotnet-linux` (default `~/.cache`), independently of the
Windows `.dotnet` SDK. It builds matching Linux Avalonia packages, the pinned
private recorder and self-contained .NET app/worker into `.local/linux-x64`.
Windows package pin 12.2.1006 is unchanged; Linux uses 12.2.1006-linux.1.

Install the native build dependencies listed in `native/gsr/UPSTREAM.md` first.
Runtime dependencies include KDE Wayland, PipeWire/PipeWire-Pulse, compatible GPU
libraries, libsqlite3, libsecret's `secret-tool`, libnotify's `notify-send`,
`busctl`, and `/usr/bin/ffmpeg` and `/usr/bin/ffprobe`. .NET is bundled; this is
not a statically linked distribution of all Linux system libraries. FFmpeg runs
as a subprocess, without loading distro FFmpeg through FFmpeg.AutoGen bindings.

Register desktop integration:

```sh
python3 .local/linux-x64/install-desktop.py
env -u DISPLAY .local/linux-x64/ClypDat --diagnose-startup
```

The private desktop entry grants KDE interface access only to this build's
absolute recorder path. Rerun registration after moving the folder. Autostart
uses `~/.config/autostart` or the absolute `XDG_CONFIG_HOME` equivalent.
Credentials use Secret Service; there is no plaintext disk fallback.

## Implemented

- Explicit `net10.0` / `linux-x64` app and worker targets, native Wayland/Skia/
  HarfBuzz packages, Linux SQLite and executable discovery.
- Per-user activation IPC, Linux autostart and desktop notification backend,
  Secret Service credentials, Discord Unix socket transport, Windows update guards.
- Linux Steam installation-root discovery (not running-game detection).
- Typed compositor capture identities, capability/readiness contract, worker
  protocol 12 rejecting older workers.
- GSR 6.1.2 pinned source with metadata-only KDE watcher patch and source notices.
  The watcher has no capture pipeline, audio graph, picker or disk-media writer.
- Explicit unavailable capture/editor states, preserving the shared UI.

## Required before the first milestone is complete

- Integrate watcher events with native/Proton process discovery and custom rules.
- Implement `kde-window:<uuid>` and `kde-output:<name>` streams using screencast
  protocol v6 object serials. Handle stream failure and output removal explicitly.
- Own a GPU frame copy and repeat it on focus loss/minimize; never change source.
- RAM replay, correlated Unix-socket commands, exact clip windows, overlapping
  saves, save IDs and finalization, full-session shared encoding and quotas.
- PipeWire application/device routing, separate tracks, exclusion from main mix,
  reconnect handling, gain/channel processing and recoverable finalization staging.
- Global shortcuts and lock/suspend recovery.
- Editor video/audio interfaces, LibVLC callback buffers and Avalonia/Skia output,
  PipeWire-Pulse playback, effects, seeking, synchronization and export validation.
- Complete runtime guards and unavailable states for deferred controls; resolve
  video-overlay binding diagnostics seen during native startup.
- Windows native build/runtime tests and full Linux machine acceptance.

Deferred parity: camera/input overlays, computer-vision highlights, remaining
highlight integrations, microphone noise processing, device-only chat isolation,
HDR, other desktops/GPUs, and release/update packaging.

## Verification

```sh
./eng/dotnet-linux.sh test native/tests/ClypDat.Linux.Tests/ClypDat.Linux.Tests.csproj -p:ClypDatLinux=true -c Release
python3 native/gsr/tests/test-watcher.py .local/linux-x64/libexec/clypdat-gsr
# From clypdat-avalonia:
../clypdat-app/eng/dotnet-linux.sh test --project tests/Avalonia.Wayland.UnitTests/Avalonia.Wayland.UnitTests.csproj -c Release -p:AvsSkipBuildingLegacyTargetFrameworks=True
```

Watcher tests use an isolated synthetic compositor, with no connection to the
user's desktop. They cover identity/escaping, focus/minimize, resize, destruction,
missing protocol and source loss. App tests cover protocol rejection, identity
validation, activation IPC and unavailable capture/update guards.

Linux can cross-compile Windows managed code with `-r win-x64
-p:Platform=x64 -p:EnableWindowsTargeting=true -p:ClypDatManagedValidation=true`. That property
skips MSVC targets and explicitly prohibits publishing; it is not a Windows
native build or runtime test.

No gameplay capture, track-tone tests, frame-freeze tests, replay RAM/write
measurements, session finalization or editor playback acceptance has passed.
User gameplay checks remain necessary after those implementations exist.

Verification on 2026-09-14: 11 Linux app tests and 41 Avalonia Wayland tests
passed; isolated watcher fixtures passed. Linux app/worker and private GSR
built, self-contained loader checks passed, and `DISPLAY`-unset startup reached
the main window. Windows app/test assemblies cross-compiled successfully.
Windows native/runtime validation was not available on this Linux host.
