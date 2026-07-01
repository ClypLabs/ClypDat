# EVE

EVE means Easy Video Editor. This MVP opens MKV files, detects audio tracks, lets you trim by time, set volume per audio track, and export to MP4 with ffmpeg.

## Native Migration

EVE is moving from Electron to C#/.NET with Avalonia UI so the app can grow into a native clipping application while keeping a Linux path open.

The new native scaffold lives in `native/`:

- `Eve.App`: Avalonia desktop UI.
- `Eve.Core`: shared clip/settings logic.
- `Eve.Capture.Abstractions`: replay-buffer and active-game detection contracts.

The current Electron app remains available while the native version catches up.

## Requirements

- Node.js
- pnpm
- ffmpeg and ffprobe available on PATH

For the native app:

- .NET SDK 8 or newer

## Run

```powershell
pnpm install
pnpm start
```

Native app scaffold:

```powershell
dotnet restore native\EVE.Native.sln
dotnet run --project native\src\Eve.App\Eve.App.csproj
```

## MVP Scope

- Open one MKV file at a time.
- Preview the file using Electron's video element.
- Show detected video, audio, and subtitle track counts.
- Enable or disable each audio track.
- Set each selected audio track volume from 0% to 200%.
- Trim with start and end seconds.
- Export MP4 using AAC audio.

Some MKV codecs cannot be copied into MP4. If export fails with video copy enabled, try checking "Re-encode video with H.264".
