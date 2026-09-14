# Private KDE recorder

GPU Screen Recorder 6.1.2, commit `af736d4c38789f6c6f4b2236b9ac2a03c186703d`.
Source: https://repo.dec05eba.com/gpu-screen-recorder
License: GPL-3.0-only; COPYING and individual upstream headers govern the source.

`build.sh` checks out that commit and applies `patches/0001-kde-window-metadata.patch`,
then copies the maintained `extension/` and `protocols/` files. It never changes the
system recorder, capabilities or GPU driver settings. Publish ships the upstream
archive, patch, extension, protocols, fixtures, build script and notices together.

Build dependencies: C/C++ compilers, Git, Python with venv/pip, pkg-config,
wayland-scanner, Wayland development files, FFmpeg development libraries, PipeWire,
PulseAudio, X11, DRM, VA-API, D-Bus and libcap. When absent, Meson 1.10.2 and Ninja
1.13.0 are bootstrapped into `$XDG_CACHE_HOME/clypdat/gsr-tools-1.10.2` (default
`~/.cache`). Missing Vulkan headers are fetched at KhronosGroup/Vulkan-Headers
commit `ee2ec5fd83dafce291024683b50dc89219333076`. No previous `/tmp` build tools
are required. Distro library/compiler versions remain build prerequisites; this
is a reproducible source procedure, not a claim of bit-identical binaries.

Private commands:

- `--clypdat-capabilities`: version 2, implemented capture/replay/session/timeline capabilities.
- `--clypdat-watch-windows`: metadata JSON-lines version 1, UUID/app ID/PID/title,
  focus/minimize and geometry. Disconnect exits nonzero; it records no media.
- `--clypdat-list-outputs`: actual Wayland output names and logical geometry.
- `-w kde-window:<uuid>` / `-w kde-output:<name>`: KDE screencast v6, targeting the
  full 64-bit PipeWire object serial. A missing source never falls back to another.
- KDE capture owns a GPU frame copy, repeated on focus loss/minimize. Audio and
  recorder time continue. Source loss is fatal and the worker reacquires it.
- RAM replay uses the existing packet ring and explicit Unix-socket IPC. Save
  staging includes a recorder-time `.timeline.json`. Packet/trailer errors fail
  the completion. Full sessions share the replay encoder.
- PipeWire routing adds executable, application ID and PID selectors; links are
  owned and deduplicated. Executable selections follow late/recreated nodes.

Both KDE protocol XML files are from plasma-wayland-protocols commit
`382dfabda886d3f2f5c067b22e5a22376685ba78`, retaining their copyright/license
notices: https://github.com/KDE/plasma-wayland-protocols/tree/382dfabda886d3f2f5c067b22e5a22376685ba78

Synthetic tests live in `tests/`. Run the four `test-*.py` scripts from the source
checkout after building; `test-watcher.py` takes the built recorder path. They
use private compositor fixtures, memory packets and synthetic mux failures,
without capturing the user's desktop. They do not replace live GPU/PipeWire
and gameplay acceptance described in the app's `docs/linux-support.md`.
