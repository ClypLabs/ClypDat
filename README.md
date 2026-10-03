# ClypDat

ClypDat records game or desktop footage on Windows. It keeps a rolling replay buffer, so pressing a hotkey saves what just happened. You can also record a full session, save clips automatically for supported game events, and edit clips in the local library.

Version 2.0 uses a native C++ recorder. The desktop UI is C#/.NET 10 with a maintained [Avalonia fork](https://github.com/ClypLabs/clypdat-avalonia).

<img width="2661" height="1811" alt="ClypDat library showing recorded game clips" src="https://github.com/user-attachments/assets/70e5d97a-c3cf-4328-a00a-630e07a71ebf" />

## Install

Download from [clypdat.xyz](https://www.clypdat.xyz) or [GitHub Releases](https://github.com/ClypLabs/ClypDat/releases/latest). Choose **Setup.exe** for a normal installation. Portable builds run without installation; MSI and ZIP packages are also available.

The unversioned `ClypDat-Setup.exe` is the same installer as the versioned Setup file. Its permanent filename keeps existing updater and WinGet links working. FFmpeg source archives and signed release manifests are supporting files, not additional programs to install.

ClypDat checks for stable updates and verifies signed download metadata. First-run setup asks you to choose **Game Capture** or **Desktop Capture** before recording starts.

Install with WinGet:

```powershell
winget install --id ClypLabs.ClypDat
```

The [WinGet package](https://github.com/microsoft/winget-pkgs/tree/master/manifests/c/ClypLabs/ClypDat) updates after its community manifest is accepted.

## Recording

Windows Graphics Capture is the primary capture path, with Desktop Duplication available as a fallback. Capture, scaling, video encoding and recording overlays run through the native recorder. Available codecs depend on your hardware:

| Encoder | Recording codecs |
|---|---|
| NVIDIA NVENC | H.264, AV1 on supported GPUs |
| AMD AMF | H.264, AV1 on supported GPUs |
| Intel Quick Sync | H.264 |
| Software libx264 | H.264 |

Set resolution, frame rate, quality, replay duration and hotkeys in Settings. Individual games can override these settings. On a supported NVIDIA card, automatic hardware recording uses NVENC.

[OSC network controls](docs/osc.md) can save clips and change replay length over UDP. Enable them under **Settings > Replay Buffer**; the guide includes Command Prompt examples and access/firewall details.

**Game Capture** follows the detected game window. **Desktop Capture** records the selected display. Add an unlisted game through **Settings > Game Detection** by choosing a running process or its executable. Detection also uses ClypDat's game catalog and local Steam library manifests.

**Full Session Recording** writes a separate recording alongside the replay buffer. After a graphics-device interruption, recovery starts a fresh replay buffer and an active full-session recording continues in a new file.

Auto-clip settings cover Counter-Strike 2, Dota 2, League of Legends, Fortnite, Helldivers 2 and Overwatch. Events and setup vary by game. CS2 and Dota use Game State Integration. HUD detectors are configurable per game; Fortnite and Helldivers 2 detection is off by default. Grouped events save the highest milestone rather than a separate clip for every rapid kill.

## Audio

Game, Chat and Mic audio can be recorded as separate tracks. Choose application and microphone sources, then adjust their levels in Settings. **All system audio** includes playback outside the selected game.

Clips open with an **All Tracks** mix by default. The editor also lets you listen to individual tracks and set their volumes for export.

## Library and editor

The library groups clips by date and filters by game or clip type. Cards show thumbnails and durations, with hover previews. Opening a clip loads missing metadata without requiring a second click. The New Clips popup supports selecting recent clips and shows what will be deleted before removal.

Right-click a card to rename, export, delete or open its folder. Renaming changes the library label, not the original filename or detected game. Import existing clips through **Settings > Import Clips**, using supported local catalogs or capture folders. You can copy or move the clips into the library.

The editor supports trimming, thumbnails, waveforms, overlays, blur and audio levels. **Save Trim** replaces the source with the trimmed range while retaining separate audio tracks. **Export** mixes selected tracks into an MP4 audio stream. GPU encoding is used where supported, with CPU encoding available. Exported audio is rendered independently of preview playback.

Video playback uses LibVLC with native video output; audio uses NAudio/WASAPI. Graphics-device recovery retains editor changes and playback position.

## Optional connections

Recording and editing do not require a ClypDat account. Linking one under **Settings > Connected Accounts** lets signed-in saves count toward your account stats. The account token is encrypted locally with Windows DPAPI.

Optional Xbox activity can label desktop clips, Spotify can add listening details to saved clips, and Discord Rich Presence can show your activity. Clips remain in your local library unless you choose to share them.

## Requirements

- Windows 10 or Windows 11, x64
- A graphics driver that supports the selected capture and encoder paths

Release downloads include their .NET runtime. A separate .NET runtime installation is not required.

## Build from source

Install Git, Visual Studio C++ x64 build tools, CMake and the Windows 11 SDK. The native build supports Visual Studio 2022 or 2026. `dotnet.ps1` downloads the pinned .NET SDK, so the first build needs internet access.

Clone the application and Avalonia fork into sibling directories, then use the pinned fork revision:

```powershell
git clone https://github.com/ClypLabs/ClypDat.git clypdat-app
git clone https://github.com/ClypLabs/clypdat-avalonia.git clypdat-avalonia
cd clypdat-app
$pin = [xml](Get-Content eng/AvaloniaPin.props -Raw)
$avaloniaCommit = $pin.SelectSingleNode('//ClypDatAvaloniaStableCommit').InnerText
git -C ../clypdat-avalonia checkout --detach $avaloniaCommit
./build.ps1 local -PackagesOnly
./dotnet.ps1 build native/ClypDat.Native.sln -c Release -p:Platform=x64
```

To publish, install and launch the current checkout:

```powershell
./build.ps1 local
```

`local` keeps the current branch. To publish files without installing them:

```powershell
./dotnet.ps1 publish native/src/ClypDat.App/ClypDat.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o native/publish/win-x64-folder
```

The recorder is in `native/capture-native/`, native video output in `native/video-output-native/`, and managed applications and services in `native/src/`. Normal builds use the pinned FFmpeg package. Its source recipe and dependency locks are in [eng/ffmpeg/](eng/ffmpeg/README.md).

## License

ClypDat is licensed under GPLv3. See [LICENSE](LICENSE). Distributed builds bundle LibVLC under LGPL-2.1-or-later and FFmpeg under GPLv3-or-later; [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md) lists bundled components and source locations. Releases include the matching FFmpeg source archive.

The ClypDat and ClypLabs names and visual branding are not licensed by GPLv3. See [TRADEMARK_POLICY.md](TRADEMARK_POLICY.md) for forks and modified versions.
