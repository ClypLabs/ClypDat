# EVE - Easy Video Editor

EVE records a rolling buffer of gameplay on Windows and saves the last N
seconds to a file when you press a hotkey. It also has a built-in editor for
trimming clips and mixing audio tracks before export.

The active codebase is `native/` (C#/.NET 8, Avalonia UI). An Electron
prototype still sits at the repo root; it predates the native app and is not
where development happens.

## Capture

Two capture backends, switchable in Settings:

- **OBS**: a trimmed OBS Studio runtime (32.1.2) loaded through a custom
  C++ bridge (`native/src/Eve.ObsBridge`) via `LoadLibrary`/`GetProcAddress`,
  not static linking. Uses NVENC for encoding.
- **Windows Capture**: `ScreenRecorderLib`, backed by Windows Graphics
  Capture / DXGI desktop duplication. Doesn't inject into the target
  process, so anti-cheat can't block it the way it blocks OBS's hook.

Foreground-window polling drives game detection: a catalog of known
executables plus a fallback that accepts any window whose
process has loaded a Direct3D, OpenGL, or Vulkan module. Saved clips are
named after the detected game and timestamp, e.g.
`Counter-Strike 2 2026-07-10 17-30-00.mp4`.

## Editor

Trim start/end, set per-track audio volume, scrub a thumbnail preview, view
a waveform, export to MP4. Video playback runs on LibVLC; audio runs on a
separate NAudio/WASAPI pipeline. They are not synchronized to a shared
clock, so long clips can drift out of sync during playback.

## Auto-update

On launch, EVE checks the GitHub Releases API for a newer non-draft,
non-prerelease tag. If found, it shows a dialog with the version and release
notes; accepting downloads `EVE-win-x64.zip`, extracts it, and replaces the
running install via a PowerShell helper that waits for the process to exit.

## Requirements

- Windows 10 or 11, x64
- .NET SDK 8+ to build from source
- An NVIDIA GPU. The OBS backend's encoder is NVENC-only; there is no
  software or non-NVIDIA fallback.

## Building

```powershell
dotnet restore native\EVE.Native.sln
dotnet build native\EVE.Native.sln
```

`Eve.ObsBridge` is a C++ project and needs MSBuild, not `dotnet build`:

```powershell
msbuild native\src\Eve.ObsBridge\Eve.ObsBridge.vcxproj /p:Configuration=Release /p:Platform=x64
```

To produce a runnable, self-contained build:

```powershell
dotnet publish native\src\Eve.App\Eve.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o native\publish\win-x64-folder
```

`EVE.exe` and its dependencies land in `native\publish\win-x64-folder`.
Pushing a tag matching `v*` triggers `.github/workflows/release.yml`, which
builds the same output and packages it four ways: a zip, a self-extracting
portable exe, an NSIS installer, and an MSI.


## Future Updates

- Will add support to use AMD GPU's for OBS
- Will also add auto-clipping functionalitys for games
- Update UI, make it look more polished
- I'll update this when I get more ideas lol


## Known limitations

- Windows only.
- NVENC-only encoding in the OBS backend.

## Third-party licenses

EVE bundles OBS Studio (GPLv2) and LibVLC (LGPL-2.1-or-later) binaries.
`THIRD-PARTY-LICENSES.md` lists what's bundled, under which license, and
where to get matching source. `native/vendor/obs-runtime` is not a full OBS
Studio install; it's trimmed to the six plugins the bridge actually loads
(`win-capture`, `win-wasapi`, `image-source`, `obs-ffmpeg`, `obs-nvenc`,
`text-freetype2`).

## License

GPLv3. See `LICENSE`. Third-party components bundled in distributed builds
(OBS, LibVLC, ffmpeg) carry their own licenses; see
`THIRD-PARTY-LICENSES.md`.
