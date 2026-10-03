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
the **MIT License**. ClypDat ships a fork of it built from
https://github.com/ClypLabs/clypdat-avalonia.

- Project: https://github.com/AvaloniaUI/Avalonia
- The licence text is in `licenses/Avalonia-LICENSE.md`.

## SkiaSharp and Skia (MIT; bundled native libraries under their own licences)

Avalonia renders through SkiaSharp (`libSkiaSharp.dll`), which incorporates
Google's Skia (BSD-3-Clause) and further native libraries.

- Project: https://github.com/mono/SkiaSharp
- SkiaSharp's MIT licence is in `licenses/SkiaSharp/LICENSE.txt`; the notices
  for every library compiled into the native binary (Skia, libpng, libjpeg-turbo,
  libwebp, expat, zlib and others) are in `licenses/SkiaSharp/THIRD-PARTY-NOTICES.txt`.

## HarfBuzzSharp and HarfBuzz (MIT)

Text shaping uses HarfBuzzSharp (`libHarfBuzzSharp.dll`), which incorporates
HarfBuzz.

- Project: https://github.com/mono/SkiaSharp (HarfBuzzSharp) and https://github.com/harfbuzz/harfbuzz
- Licence: `licenses/HarfBuzzSharp/LICENSE.txt`; native notices:
  `licenses/HarfBuzzSharp/THIRD-PARTY-NOTICES.txt`.

## ANGLE (BSD-3-Clause)

Avalonia's GPU rendering on Windows uses ANGLE (`av_libglesv2.dll`, from
`Avalonia.Angle.Windows.Natives`).

- Project: https://chromium.googlesource.com/angle/angle
- Licence: `licenses/ANGLE/LICENSE`.

## Inter typeface (SIL Open Font License 1.1)

ClypDat's interface font is Inter by Rasmus Andersson, embedded via
`Avalonia.Fonts.Inter`. The font software is licensed under the **SIL Open Font
License, Version 1.1**, which requires the licence to accompany the font.

- Project: https://github.com/rsms/inter
- Copyright notice and full licence text: `licenses/Inter-OFL-1.1.txt`.

## .NET runtime (MIT)

ClypDat is published self-contained, so it ships the .NET and Windows Desktop
runtimes.

- Project: https://github.com/dotnet/runtime
- Licence: `licenses/dotnet/LICENSE.TXT` and `licenses/dotnet/WindowsDesktop-LICENSE`;
  third-party notices for code inside the runtime: `licenses/dotnet/THIRD-PARTY-NOTICES.TXT`.

## SQLitePCLRaw (Apache-2.0) and Microsoft.Data.Sqlite (MIT)

The clip library cache uses Microsoft.Data.Sqlite over SQLitePCLRaw, bound to
Windows' own `winsqlite3` (no SQLite binary is bundled).

- Projects: https://github.com/ericsink/SQLitePCL.raw and https://github.com/dotnet/efcore
- SQLitePCLRaw: Copyright 2014-2025 SourceGear, LLC, **Apache License 2.0**,
  text in `licenses/Apache-2.0.txt`. Microsoft.Data.Sqlite: MIT, see
  `licenses/MIT-components.txt`.

## Other MIT runtime libraries

SharpGen.Runtime, MicroCom.Runtime and Tmds.DBus.Protocol (Avalonia
dependencies) are MIT-licensed. Their copyright notices and the MIT text are in
`licenses/MIT-components.txt`.

## NAudio (MIT)

ClypDat's audio capture/mixing (editor playback and the Windows Capture
backend's audio routing) uses NAudio, licensed under the **MIT License**.

- Project: https://github.com/naudio/NAudio
- Licence: `licenses/NAudio-LICENSE.txt`.

## Markdig (BSD-2-Clause)

Release-note formatting uses Markdig by Alexandre Mutel, licensed under the
**BSD 2-Clause License**.

- Project: https://github.com/xoofx/markdig
- The BSD-2-Clause text is included in `licenses/Markdig-BSD-2-Clause.txt`.

## FFmpeg 8.1.2 shared build (GPL-3.0-or-later)

ClypDat distributes one coherent FFmpeg 8.1.2 shared package for the native recorder and bundled `ffmpeg.exe` / `ffprobe.exe`. Its FFmpeg source commit is `38b88335f99e76ed89ff3c93f877fdefce736c13`. The build recipe, locked dependencies and reproducibility instructions are in `eng/ffmpeg/`. The FFmpeg DLLs, command-line tools, matching SDK headers/import libraries and oneVPL dispatcher originate from the same accepted package.

- Binary package SHA-256: `064c2f354be3cd5ccdd17fc6a9026ca6ece27101b454394b8d4d56d55de4bec6` (`clypdat-ffmpeg-8.1.2-win64-shared-r3.zip`).
- Matching source package SHA-256: `03289ac6a72fa6bd825f4de2cab4781388e5704a0fa74846f53c5889ad13b499` (`clypdat-ffmpeg-8.1.2-win64-shared-r3-sources.zip`). The source ZIP is included in the repository under `eng/ffmpeg/artifacts/` and published alongside each release's installers.
- The runtime's individual file hashes and sizes are in `native/vendor/ffmpeg/runtime-manifest.json`. Build and publish verification reject missing, changed or extra runtime files.
- The build enables `--enable-gpl --enable-version3` and has no `--enable-nonfree`; the combined FFmpeg binaries are **GPL-3.0-or-later**, matching ClypDat's GPLv3 distribution. FFmpeg's original license texts and notices ship in `licenses/ffmpeg/`.
- oneVPL **2.16.0** uses one imported `libvpl.dll` with consistent experimental ABI settings. Its MIT notice is `licenses/ffmpeg/onevpl/LICENSE`.
- libopus **1.5.2**, libvorbis **1.3.7** and libogg **1.3.6** (BSD-3-Clause) are linked into avcodec for Opus and Vorbis recording audio. Their notices are `licenses/ffmpeg/opus/COPYING`, `licenses/ffmpeg/vorbis/COPYING` and `licenses/ffmpeg/ogg/COPYING`.
- x264, x265, libaom, dav1d, zlib, AMD AMF and NVIDIA codec header notices, the LLVM compiler notice, IJG acknowledgement and Microsoft runtime provenance are in `licenses/ffmpeg/`. See `eng/ffmpeg/LICENSE-REVIEW.md` for the exact enabled libraries and obligations. LLVM clang-cl is a pinned build tool for libaom only; no LLVM runtime library is bundled.
- The complete GPLv3 text is in ClypDat's `LICENSE`. Original FFmpeg/codec sources and patches, the pinned build recipe and matching license material are in the source ZIP. See https://ffmpeg.org/legal.html for upstream component licensing.

## Vortice.Windows (MIT)

ClypDat's Direct3D11/DXGI/DirectComposition interop for its overlay windows,
editor preview, monitor thumbnails and HDR display checks
(`Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.DirectComposition`) uses
Vortice.Windows, licensed under the **MIT License**.

- Project: https://github.com/amerkoleci/Vortice.Windows
- Copyright notice and MIT text: `licenses/MIT-components.txt`.

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
