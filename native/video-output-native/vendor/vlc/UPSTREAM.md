# VLC D3D11 output source

Vendored from VideoLAN VLC tag `3.0.23-2`, commit
`79128878ddb2c280bbb6c89c76a46b31a80ade1c`:
https://github.com/videolan/vlc-3.0/tree/79128878ddb2c280bbb6c89c76a46b31a80ade1c

This is the core revision bundled by `VideoLAN.LibVLC.Windows` NuGet **3.0.23.1**.
The x64 `libvlccore.dll` SHA-256 is
`d3475b834dd3eb77910f37f71b0341d358bcbdda5b9f04cc4a3a8e2be1bc8e35`.
Build and managed loader reject other cores because VLC's output ABI is private.

Copyright and author notices remain in each source file. These files are licensed
under LGPL-2.1-or-later; see `COPYING.LIB`. ClypDat's modifications to this output
and its native bridge/compositor are also available under LGPL-2.1-or-later.
Complete modified source and build instructions are in `native/video-output-native`
in https://github.com/ClypLabs/ClypDat (fallback: https://gitlab.com/clypdat-group1/ClypDat-App).

ClypDat modifications, September 2026:

- `direct3d11.c`: editor context selection, original-size decoded texture retention,
  composition before presentation, paused redraw, status reporting and failure handling.
- Private D3D11, chroma and window helpers: MSVC-compatible array parameters,
  variadic macros, stack allocation and nonempty structs.
- `compat/vlc_atomic.h`: copy of the bundled SDK header with MSVC C11 atomics enabled.
- `compat/msvc.h`: SDK compatibility definitions and allocator routing. Vendored C
  allocations use `msvcrt.dll`, matching bundled MinGW VLC. C++ compositor allocations
  remain owned by the MSVC runtime. Crossing these heaps corrupts picture pools.

No VLC core binary is modified. The bridge loads the same plugin path used by VLC,
keeps the module loaded for process lifetime, and releases per-editor contexts at teardown.
