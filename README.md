# EVE - Easy Video Editor

EVE records a rolling buffer of gameplay on Windows and saves the last N
seconds to a file when you press a hotkey. It also has a built-in editor for
trimming clips and mixing audio tracks before export.

The active codebase is `native/` (C#/.NET 8, Avalonia UI). An Electron
prototype still sits at the repo root; it predates the native app and is not
where development happens. (Electron prototype will probably get shit-canned tomorrow)

## Capture

Three capture backends, switchable in Settings (default is Auto):

- **EVE (Native)**: EVE's own capture engine, built directly on DXGI Desktop
  Duplication with GPU-side downscaling (`native/src/Eve.App/Services/NativeReplayBuffer.cs`).
  No process hook, so anti-cheat can't object to it, with true per-window
  capture that keeps recording through alt-tabs/overlays and no stop/start
  gap between rolling-buffer segments. Encodes with NVENC, falling back to
  AMD AMF, then software libx264, so it isn't NVIDIA-only. Selected
  automatically on Auto.
- **OBS**: a trimmed OBS Studio runtime (32.1.2) loaded through a custom
  C++ bridge (`native/src/Eve.ObsBridge`) via `LoadLibrary`/`GetProcAddress`,
  not static linking. Uses NVENC for encoding.
- **Windows Capture (Legacy)**: `ScreenRecorderLib`, backed by Windows
  Graphics Capture / DXGI desktop duplication. Doesn't inject into the
  target process either, kept around as a fallback alongside EVE's own engine.

Foreground-window polling drives game detection: a catalog of known
executables plus a fallback that accepts any window whose
process has loaded a Direct3D, OpenGL, or Vulkan module. Saved clips are
named after the detected game and timestamp, e.g.
`Counter-Strike 2 2026-07-10 17-30-00.mp4`. Games can also be added from
Settings > Game Detection by picking a currently-running process or
browsing for an executable directly.

## Auto-clipping

Currently EVE only has CS2 auto-clipping (Experimental), so expect issues!

For CS2, EVE listens to the game's own Game State Integration feed (no
screen/voice analysis) and can automatically save a clip on kills, a
headshot, a death, or an assist. Rapid kills within a debounce window
are coalesced into a single clip for the final milestone (e.g. a 3K
followed quickly by a 4K only saves once, as the 4K) instead of one
clip per kill.

## Full Session recording

Optionally records the entire time the replay buffer is running to a
single file (Settings > Replay Buffer > Full Session Recording), separate
from the rolling clip buffer, which keeps working at the same time. Audio
is periodically resynced in 60s chunks so multi-hour sessions don't drift.
If the game window loses focus mid-session (or mid-clip), the last real
frame freezes instead of recording whatever's now on screen, and the
editor shows a "Recording Paused" badge over those stretches.

## Importing from Medal

Settings > Import from Medal scans Medal's local database for clips and
copies (or moves) them into your EVE library, keeping Medal's own titles.
If Medal's database is missing or corrupted, it also falls back to
scanning Medal's default clips folder directly so nothing gets lost.
Medal's auto-generated "{date} - {time} - {game}" names are parsed back
into the real game name and recording date instead of being used verbatim,
and imported cards show "Imported from Medal".

## First-time setup

A one-time interactive setup wizard runs on first launch (library
folder, hotkey, replay length, capture backend, audio) and can be
replayed any time from Settings > About > Show Walkthrough.

## Editor

Trim start/end, set per-track audio volume (including separate chat/mic
tracks), scrub a thumbnail preview, view a waveform, export to MP4. Video
playback runs on LibVLC; audio runs on a separate NAudio/WASAPI pipeline.
They are not synchronized to a shared clock, so long clips can drift out
of sync during playback.

Export mixes all audio tracks down to one (with each track's volume
applied) so the file plays everywhere; Save Trim instead re-encodes the
trimmed range over the original clip in place, keeping Game/Chat/Mic as
separate tracks so it stays fully editable. Both encode on the GPU via
NVENC (H.264/H.265/AV1) with an automatic CPU fallback, and show a
progress popup with a live percentage, time estimate, and Cancel.

The Library shows per-card date headers and has Game Filters and Clip
Type Filters dropdowns in the header (each option shows its clip count),
plus a right-click context menu on clip cards (rename, export, delete,
open location). Renaming edits the card's display label only - the game
name and original file stay untouched.

## Auto-update

On launch, EVE checks the GitHub Releases API for a newer non-draft,
non-prerelease tag. If found, it shows a dialog with the version and release
notes; accepting downloads `EVE-win-x64.zip`, extracts it, and replaces the
running install via a PowerShell helper that waits for the process to exit.

## Requirements

- Windows 10 or 11, x64
- .NET SDK 8+ to build from source
- The EVE (Native) backend works on NVIDIA, AMD, and (as a last-resort
  software fallback) any GPU-less machine. The OBS backend's encoder is
  still NVENC-only, with no AMD/software fallback.

## Known limitations

- Windows only.
- NVENC-only encoding in the OBS backend specifically.
- Clips on Network Drives tend to take far longer than clips on a regular drive. Working on a resolution.

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

- AMD/software fallback for the OBS backend too (Native already has it)
- Seamless replay buffer rotation for the Legacy Windows Capture backend
  (no stop/restart gap between segments - EVE's own backend already has this)
- I'll update this when I get more ideas lol

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
