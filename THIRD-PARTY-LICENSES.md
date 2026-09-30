# Third-Party Licenses

ClypDat bundles the following third-party components in its distributed builds
(zip, portable exe, installer, and MSI). This file exists to satisfy their
license terms, particularly the source-availability and notice requirements
of the GPL/LGPL-licensed components below.

## LibVLC / LibVLCSharp (LGPL-2.1-or-later)

ClypDat's editor playback uses LibVLC (via the `VideoLAN.LibVLC.Windows` and
`LibVLCSharp` / `LibVLCSharp.Avalonia` NuGet packages), licensed under the
**GNU Lesser General Public License v2.1 or later**.

- Project: https://code.videolan.org/videolan/vlc and https://code.videolan.org/videolan/LibVLCSharp
- LibVLC is used as a dynamically-loaded shared library (`libvlc.dll`),
  consistent with LGPL's linking terms.
- The LGPL-2.1 text is included below.

## ClypDat D3D11 video-output plugin (LGPL-2.1-or-later)

The editor bundles a modified VideoLAN D3D11 output pinned to VLC commit
`79128878ddb2c280bbb6c89c76a46b31a80ade1c` (the core in NuGet 3.0.23.1).
Original notices, modified source, native compositor, bridge and build scripts
are included in `native/video-output-native` in this repository. See its
`vendor/vlc/UPSTREAM.md` for provenance and modification details. Distributed
builds include that notice and the LGPL text under `licenses/`.

## Avalonia UI (MIT)

ClypDat's user interface is built on the Avalonia UI framework, licensed under
the **MIT License**.

- Project: https://github.com/AvaloniaUI/Avalonia

## NAudio (MIT)

ClypDat's audio capture/mixing (editor playback and the Windows Capture
backend's audio routing) uses NAudio, licensed under the **MIT License**.

- Project: https://github.com/naudio/NAudio

## Markdig (BSD-2-Clause)

Release-note formatting uses Markdig by Alexandre Mutel, licensed under the
**BSD 2-Clause License**.

- Project: https://github.com/xoofx/markdig
- The BSD-2-Clause text is included in `licenses/Markdig-BSD-2-Clause.txt`.

## FFmpeg 8.1.2 shared build (GPL-3.0-or-later)

ClypDat distributes one coherent FFmpeg 8.1.2 shared package for the native recorder and bundled `ffmpeg.exe` / `ffprobe.exe`. Its FFmpeg source commit is `38b88335f99e76ed89ff3c93f877fdefce736c13`. The build recipe, locked dependencies and reproducibility instructions are in `eng/ffmpeg/`. The FFmpeg DLLs, command-line tools, matching SDK headers/import libraries and oneVPL dispatcher originate from the same accepted package.

- Binary package SHA-256: `c3fa9bf41f61658c046c286b7f020d0997b891c49a50a1eaba2c4dc6ec3f951f` (`clypdat-ffmpeg-8.1.2-win64-shared-r2.zip`).
- Matching source package SHA-256: `2a41968c8e47a6e0b3b067c40bd43209af4539a89db712fae631a0d032943532` (`clypdat-ffmpeg-8.1.2-win64-shared-r2-sources.zip`). The source ZIP is included in the repository under `eng/ffmpeg/artifacts/` and published alongside each release's installers.
- The runtime's individual file hashes and sizes are in `native/vendor/ffmpeg/runtime-manifest.json`. Build and publish verification reject missing, changed or extra runtime files.
- The build enables `--enable-gpl --enable-version3` and has no `--enable-nonfree`; the combined FFmpeg binaries are **GPL-3.0-or-later**, matching ClypDat's GPLv3 distribution. FFmpeg's original license texts and notices ship in `licenses/ffmpeg/`.
- oneVPL **2.16.0** uses one imported `libvpl.dll` with consistent experimental ABI settings. Its MIT notice is `licenses/ffmpeg/onevpl/LICENSE`.
- x264, x265, libaom, dav1d, zlib, AMD AMF and NVIDIA codec header notices, the LLVM compiler notice, IJG acknowledgement and Microsoft runtime provenance are in `licenses/ffmpeg/`. See `eng/ffmpeg/LICENSE-REVIEW.md` for the exact enabled libraries and obligations. LLVM clang-cl is a pinned build tool for libaom only; no LLVM runtime library is bundled.
- The complete GPLv3 text is in ClypDat's `LICENSE`. Original FFmpeg/codec sources and patches, the pinned build recipe and matching license material are in the source ZIP. See https://ffmpeg.org/legal.html for upstream component licensing.

## Vortice.Windows (MIT)

ClypDat's Direct3D11/DXGI/DirectComposition interop for its overlay windows,
editor preview, monitor thumbnails and HDR display checks
(`Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.DirectComposition`) uses
Vortice.Windows, licensed under the **MIT License**.

- Project: https://github.com/amerkoleci/Vortice.Windows

---

## GPLv2 full text

A copy of the GNU General Public License v2.0 is available at
https://www.gnu.org/licenses/old-licenses/gpl-2.0.html and is reproduced
in `licenses/GPL-2.0.txt` in this repository.

## LGPL-2.1 full text

A copy of the GNU Lesser General Public License v2.1 is available at
https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html and is reproduced
in `licenses/LGPL-2.1.txt` in this repository.

## RNNoise and model `lq.rnnn` (BSD-3-Clause; model licence not asserted)

`rnnoise/lq.rnnn` is the "leavened-quisling" model from
https://github.com/GregorR/rnnoise-models, trained with the rnnoise-nu tools
for a voice signal against general background noise. It is loaded by ffmpeg's
`arnndn` filter to drive microphone noise suppression.

That repository carries no LICENSE file and declares no licence. Its README
states: "With the exception of the tools/ directory and this file, none of this
work is creative and thus none of it is subject to copyright." That is the
author's own position on copyrightability, not a formal licence or a
public-domain dedication, and it is reproduced here as such rather than
restated as one. The model file is shipped byte-for-byte unmodified.

RNNoise is licensed under BSD-3-Clause. Its full license text is reproduced in
`licenses/RNNoise-BSD-3-Clause.txt`. The `arnndn` filter that consumes the
model is part of ffmpeg's libavfilter, already covered by the ffmpeg entry
above.
