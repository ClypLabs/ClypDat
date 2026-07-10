# EVE

EVE is a Windows replay buffer and clip editor. It records a rolling buffer of
gameplay in the background, saves the last N seconds on a hotkey press, and
gives you a built-in editor to trim, mix audio tracks, and export the result.

The active codebase is the native app in `native/` (C#/.NET 8, Avalonia UI).
An older Electron prototype still exists at the repo root but is not where
development happens.

## What it does

- **Replay buffer.** Continuously records to a rotating buffer; press a
  hotkey to save the last N seconds as a clip. Duration and quality are
  configurable.
- **Capture backends.** Two capture paths, switchable in Settings:
  - **OBS** — uses a bundled, trimmed OBS Studio runtime via a custom C++
    bridge (`native/src/Eve.ObsBridge`). Best quality, lowest overhead.
  - **Windows Capture** — uses `ScreenRecorderLib` (Windows Graphics
    Capture / DXGI desktop duplication). No process hook, so it isn't
    blocked by anti-cheat. Games known to fight OBS's hook (currently
    Counter-Strike 2) default to this backend automatically on "Auto".
- **Game detection.** Foreground-window polling with a small catalog of
  known games plus a general heuristic (any window with a loaded
  DirectX/OpenGL/Vulkan module counts). Saved clips are named after the
  detected game.
- **Editor.** Trim start/end, per-track audio volume, waveform preview,
  thumbnail scrubbing, export to MP4.
- **Auto-update.** Checks GitHub Releases on launch; can download and
  install a new version without leaving the app.

## Requirements

- Windows 10/11, x64
- .NET SDK 8+ to build from source
- `ffmpeg` and `ffprobe` on `PATH` (used for muxing, probing, and
  thumbnail/waveform generation — not bundled)
- An NVIDIA GPU (NVENC is required for the replay encoder; there is no
  software-encoding fallback in the OBS backend)

## Building

```powershell
dotnet restore native\EVE.Native.sln
dotnet build native\EVE.Native.sln
```

The OBS bridge is a separate C++ project and needs MSBuild, not `dotnet
build`:

```powershell
msbuild native\src\Eve.ObsBridge\Eve.ObsBridge.vcxproj /p:Configuration=Release /p:Platform=x64
```

To produce a runnable, self-contained build:

```powershell
dotnet publish native\src\Eve.App\Eve.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o native\publish\win-x64-folder
```

`EVE.exe` and its dependencies (the OBS runtime, LibVLC, etc.) land in
`native\publish\win-x64-folder`. Tagged pushes (`v*`) trigger
`.github/workflows/release.yml`, which builds this same output and packages
it four ways: a zip, a self-extracting portable exe, an NSIS installer, and
an MSI.

## Project layout

```
native/
  src/
    Eve.App/                  Avalonia UI, view models, platform services
    Eve.Core/                 settings, clip-library models
    Eve.Capture.Abstractions/ IReplayBuffer and related interfaces
    Eve.ObsBridge/             C++ bridge to a dynamically-loaded OBS runtime
  vendor/
    obs-runtime/               trimmed OBS Studio runtime (committed; see below)
installer/                     NSIS and WiX installer definitions
licenses/                      full text of bundled GPL/LGPL licenses
```

## Third-party components

EVE bundles OBS Studio (GPLv2) and LibVLC (LGPL-2.1-or-later) binaries.
`THIRD-PARTY-LICENSES.md` documents what's bundled, under what license, and
where to find matching source. Both licenses' full text ship with every
build in `licenses/`.

`native/vendor/obs-runtime` is trimmed to only the OBS plugins EVE's bridge
actually loads (`win-capture`, `win-wasapi`, `image-source`, `obs-ffmpeg`,
`obs-nvenc`, `text-freetype2`) — not a full OBS Studio install.

## Known limitations

- Windows only.
- NVENC-only encoding; no CPU or non-NVIDIA GPU encode path.
- Windows Capture goes black/stale while the target window is minimized,
  and doesn't see overlay windows (Discord overlay, FPS counters) layered
  on top of the game, since it only captures that window's own content.
- The editor's audio and video playback are driven by separate engines
  (NAudio and LibVLC) with no shared clock; long clips can drift slightly
  out of sync.
