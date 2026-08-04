# ClypDat

<img width="1404" height="914" alt="ClypDat Preview" src="https://github.com/user-attachments/assets/0436efa1-f47b-4a4b-9f8c-15c255c32fd9" />

ClypDat records a rolling buffer of gameplay on Windows and saves the last few
minutes to a file when you press a hotkey. It also has a built-in editor for
trimming clips and mixing audio tracks before export.

The codebase is `native/` (C#/.NET 10, Avalonia UI).

## Capture

Two capture backends, switchable in Settings (default is Auto):

- **ClypDat (Native)**: ClypDat's own capture engine, built directly on DXGI Desktop
  Duplication with GPU-side downscaling (`native/src/ClypDat.App/Services/NativeReplayBuffer.cs`).
  No process hook, so anti-cheat can't object to it, with true per-window
  capture that keeps recording through alt-tabs/overlays and no stop/start
  gap between rolling-buffer segments. Encodes with NVENC, falling back to
  AMD AMF, then software libx264, so it isn't NVIDIA-only. Selected
  automatically on Auto.
- **Windows Capture (Legacy)**: `ScreenRecorderLib`, backed by Windows
  Graphics Capture / DXGI desktop duplication. Doesn't inject into the
  target process either, kept around as a fallback alongside ClypDat's own engine.

Foreground-window scanning drives game detection: ClypDat accepts only
known catalog entries, user-added games, and executables installed inside a
Steam library listed in Steam's local manifests. Catalog rules can match an
executable with exact or partial window titles/classes and reject known helper
or overlay windows. The catalog is bundled with the app, cached locally, and
can update from ClypDat's GitHub repository. Unknown GPU apps are not treated
as games. Saved clips are named after the detected game and timestamp, e.g.
`Counter-Strike 2 2026-07-10 17-30-00.mp4`. Games can also be added from
Settings > Game Detection by picking a currently-running process or
browsing for an executable directly.

## Auto-clipping

Currently ClypDat only has CS2 auto-clipping for the time-being but we are planning to add more!

For CS2, ClypDat listens to the game's own Game State Integration feed (no
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
copies (or moves) them into your ClypDat library, keeping Medal's own titles.
If Medal's database is missing or corrupted, it also falls back to
scanning Medal's default clips folder directly so nothing gets lost.
Medal's auto-generated "{date} - {time} - {game}" names are parsed back
into the real game name and recording date instead of being used verbatim,
and imported cards show "Imported from Medal".

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

On launch, ClypDat checks the GitHub Releases API for a newer non-draft,
non-prerelease tag. If found, it shows a dialog with the version and release
notes; accepting downloads `ClypDat-Setup.exe`, verifies it, then starts the
per-user installer after ClypDat exits.

## Requirements

- Windows 10 or 11, x64
- Building from source needs an internet connection once; ClypDat downloads its
  pinned .NET SDK into `.dotnet` inside the clone. No system-wide .NET install.
- The ClypDat (Native) backend works on NVIDIA, AMD, and (as a last-resort
  software fallback) any GPU-less machine.

## Building

```powershell
.\dotnet.ps1 restore native\ClypDat.Native.sln
.\dotnet.ps1 build native\ClypDat.Native.sln
```

To produce a runnable, self-contained build:

```powershell
.\dotnet.ps1 publish native\src\ClypDat.App\ClypDat.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o native\publish\win-x64-folder
```

`ClypDat.exe` and its dependencies land in `native\publish\win-x64-folder`.
Those files include .NET runtime; users need only ClypDat installer/portable app.
Pushing a tag matching `v*` triggers `.github/workflows/release.yml`, which
builds the same output and packages it four ways: a zip, a self-extracting
portable exe, an NSIS installer, and a raw MSI. Installers default to
`%LocalAppData%\Programs\ClypDat` without requiring UAC approval.

## Future Updates

- Seamless replay buffer rotation for the Legacy Windows Capture backend
  (no stop/restart gap between segments - ClypDat's own backend already has this)
- I'll update this when I get more ideas lol

## Third-party licenses

ClypDat bundles LibVLC (LGPL-2.1-or-later) and ffmpeg (GPL) binaries.
`THIRD-PARTY-LICENSES.md` lists bundled components, licenses, and matching
source locations.

## License

GPLv3. See `LICENSE`. Third-party components bundled in distributed builds
(LibVLC, ffmpeg) carry their own licenses; see
`THIRD-PARTY-LICENSES.md`.
