# ClypDat

<img width="2661" height="1811" alt="ClypDat library showing recorded game clips" src="https://github.com/user-attachments/assets/70e5d97a-c3cf-4328-a00a-630e07a71ebf" />

ClypDat is a Windows replay recorder for gameplay. It keeps recent footage in a rolling buffer, then saves that buffer when you press a hotkey. Clips stay in a local library where you can trim, rename, export, or delete them.

The codebase is `native/` (C#/.NET 10, Avalonia UI). ClypDat uses a maintained [Avalonia fork](https://github.com/ClypLabs/clypdat-avalonia).

## Install

Download the current installer or portable build from [GitHub Releases](https://github.com/ClypLabs/ClypDat/releases). ClypDat checks for stable releases at launch and can download the next installer after you accept an update.

### Install or update ClypDat from PowerShell with WinGet:

```powershell
winget install --id ClypLabs.ClypDat
```

WinGet package: [`ClypLabs.ClypDat`](https://github.com/microsoft/winget-pkgs/tree/master/manifests/c/ClypLabs/ClypDat).

## Capture

ClypDat uses its native replay-buffer backend with DXGI Desktop Duplication and GPU-side downscaling. It does not inject into games. NVENC is used when available, followed by AMD AMF and software `libx264`.

When ClypDat loses the game window because it is covered, backgrounded, or minimized, it freezes the last game frame instead of recording another app. Saved clips use the game name and recording time, for example `Counter-Strike 2 2026-07-10 17-30-00.mp4`.

Game detection accepts games from ClypDat's catalog, games you add yourself, and executables inside Steam libraries found from local Steam manifests. Catalog rules match an executable and window details, while excluding helper and overlay windows. Add an unlisted game from **Settings > Game Detection** by selecting a running process or browsing to its executable.

## Clips and full sessions

Set a replay duration, recording quality, encoder, audio tracks, and save hotkey in Settings. The rolling buffer stays active while you play. Press the hotkey to save the configured period before that moment.

**Full Session Recording** writes one separate file for the whole replay-buffer session. It records alongside the rolling buffer. Audio is resynced in 60-second chunks to prevent drift in long recordings.

ClypDat can also save CS2 clips automatically from the game's Game State Integration feed. It supports kills, headshots, deaths, and assists. Rapid kills are combined into one clip for the last milestone, so a 3K followed by a 4K saves the 4K once.

## Library and editor

The library groups clips by date and filters them by game or clip type. Right-click a card to rename, export, delete, or open its folder. Renaming changes the library label, not the original filename or detected game.

The editor trims footage, previews thumbnails and waveforms, and sets volume for each audio track. **Save Trim** replaces the source file with the trimmed range while retaining separate Game, Chat, and Mic tracks. **Export** mixes tracks into one MP4 audio stream for players that do not support separate tracks. Both operations use GPU encoding when possible and fall back to CPU encoding.

Video playback uses LibVLC. Audio playback uses a separate NAudio/WASAPI pipeline, so long clips can drift during editor preview. Exported files are mixed from the selected tracks rather than from that playback path.

## Import existing clips

Import clips from other popular clipping applications through **Settings > Import Clips**. ClypDat reads supported local catalogs, then scans default capture folders when a catalog is missing or unreadable. You can choose to either copy or move the clips into the ClypDat library.

## Xbox activity

Link a ClypDat account from **Settings > Connected Accounts > Link ClypDat account**. The desktop app receives a signed token, stored encrypted with Windows DPAPI, even when no Xbox account is linked. Xbox is optional; when linked and enabled, read-only activity labels Desktop Capture clips and Discord Rich Presence. **Link Xbox directly** remains available as a local fallback.

## Requirements

- Windows 10 or Windows 11, x64
- Internet access for the first source build. `dotnet.ps1` downloads the pinned .NET SDK into `.dotnet` inside the clone, so a system-wide .NET installation is not required.

The Native backend runs on NVIDIA, AMD and Intel GPUs. It can use software `libx264` when no supported hardware encoder is available.

## Build from source

```powershell
.\dotnet.ps1 restore native\ClypDat.Native.sln
.\dotnet.ps1 build native\ClypDat.Native.sln
```

Publish a self-contained Windows build:

```powershell
.\dotnet.ps1 publish native\src\ClypDat.App\ClypDat.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o native\publish\win-x64-folder
```

The output is `native\publish\win-x64-folder`. It contains `ClypDat.exe`, dependencies, and the .NET runtime.

## License

ClypDat is licensed under GPLv3. See [LICENSE](LICENSE). Distributed builds bundle LibVLC under LGPL-2.1-or-later and FFmpeg under GPL; [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md) lists each bundled component and its source location.
