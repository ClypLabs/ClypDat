# EVE

EVE means Easy Video Editor. This MVP opens MKV files, detects audio tracks, lets you trim by time, set volume per audio track, and export to MP4 with ffmpeg.

## Requirements

- Node.js
- pnpm
- ffmpeg and ffprobe available on PATH

## Run

```powershell
pnpm install
pnpm start
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
