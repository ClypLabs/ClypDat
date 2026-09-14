# Private recorder source

GPU Screen Recorder 6.1.2, commit `af736d4c38789f6c6f4b2236b9ac2a03c186703d`.
Source: https://repo.dec05eba.com/gpu-screen-recorder
License: GPL-3.0-only, see COPYING (upstream source headers govern individual files).

`build.sh` checks out this commit and applies the maintained patch in `patches/`.
It does not install the system recorder, capabilities, services, or driver settings.
Build dependencies include Meson, Ninja, Wayland development tools, Vulkan headers,
FFmpeg development libraries, PipeWire, PulseAudio, X11, DRM, VA-API, D-Bus and libcap.

The patch currently adds metadata-only `--clypdat-watch-windows` and a machine-readable
`--clypdat-capabilities` query. The watcher requires KDE window-management v17,
reports UUID/app ID/PID/title/focus/minimize/geometry, and reports source loss by
nonzero exit. It never creates a screencast, encoder, audio graph or media file.
Its JSON lines use protocol version 1. Initial snapshots use `added`, subsequent
snapshots `changed`, and destruction `removed`.

**KDE capture is not implemented yet.** Capabilities explicitly report
`kdeCapture: false`. Stock capture commands must not be used as a substitute for
`kde-window:` or `kde-output:`. The app keeps replay unavailable.

The vendored plasma-window-management XML is from KDE plasma-wayland-protocols,
commit `382dfabda886d3f2f5c067b22e5a22376685ba78`, `src/protocols/plasma-window-management.xml`.
It retains its LGPL-2.1-or-later copyright notice. Source:
https://github.com/KDE/plasma-wayland-protocols/tree/382dfabda886d3f2f5c067b22e5a22376685ba78

Distributing the recorder requires shipping its corresponding pinned source,
patches, protocol definitions, build instructions and licenses, not only a binary.
