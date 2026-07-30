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

## ScreenRecorderLib (MIT)

ClypDat's legacy Windows Capture backend uses `ScreenRecorderLib` by Sverre
Kristoffer Skodje, licensed under the **MIT License**.

- Project: https://github.com/sskodje/ScreenRecorderLib

## Avalonia UI (MIT)

ClypDat's user interface is built on the Avalonia UI framework, licensed under
the **MIT License**.

- Project: https://github.com/AvaloniaUI/Avalonia

## NAudio (MIT)

ClypDat's audio capture/mixing (editor playback and the Windows Capture
backend's audio routing) uses NAudio, licensed under the **MIT License**.

- Project: https://github.com/naudio/NAudio

## ffmpeg / ffprobe (GPL)

ClypDat bundles `ffmpeg.exe` and `ffprobe.exe` (the gyan.dev "essentials"
Windows build, version **8.1.1**) so muxing, probing, and thumbnail/
waveform generation work without a separate ffmpeg install. This build is
compiled with `--enable-gpl` and `--enable-libx264`, making the distributed
binaries GPL-licensed. ffmpeg is a combination of many components under a
mix of GPLv2, GPLv2-or-later, and GPLv3-or-later terms depending on build
configuration; see https://ffmpeg.org/legal.html for the authoritative
per-component breakdown for this exact configuration.

- Project: https://ffmpeg.org and https://github.com/FFmpeg/FFmpeg
- Build source: https://www.gyan.dev/ffmpeg/builds (see that page's "Git
  Windows builds" section for the exact commit each release is built from)
- The GPLv2 text this build is built under is reproduced in the GPLv2
  section below.
- ClypDat does not modify these binaries.

ClypDat's experimental "ClypDat" capture backend additionally bundles the
**shared-library** build of the same ffmpeg version (`avcodec-62.dll`,
`avformat-62.dll`, `avutil-60.dll`, `swscale-9.dll`, `swresample-6.dll`,
also from gyan.dev), P/Invoked directly (via `FFmpeg.AutoGen`) instead of
shelled out to as a separate process. Same GPLv2/libx264 build
configuration and terms as above; ClypDat is GPLv3-licensed itself (see
`LICENSE`), so directly linking a GPL component is not a licensing
conflict.

## Vortice.Windows (MIT)

The native capture backend's DXGI/Direct3D11 interop (`Vortice.Direct3D11`,
`Vortice.DXGI`) uses Vortice.Windows, licensed under the **MIT License**.

- Project: https://github.com/amerkoleci/Vortice.Windows

## FFmpeg.AutoGen (MIT)

The native capture backend's direct libavcodec/libavformat P/Invoke
bindings use FFmpeg.AutoGen, licensed under the **MIT License**. This
covers only the C# binding code itself; the underlying ffmpeg binaries it
calls into are covered under "ffmpeg / ffprobe (GPL)" above.

- Project: https://github.com/Ruslan-B/FFmpeg.AutoGen

---

## GPLv2 full text

A copy of the GNU General Public License v2.0 is available at
https://www.gnu.org/licenses/old-licenses/gpl-2.0.html and is reproduced
in `licenses/GPL-2.0.txt` in this repository.

## LGPL-2.1 full text

A copy of the GNU Lesser General Public License v2.1 is available at
https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html and is reproduced
in `licenses/LGPL-2.1.txt` in this repository.
