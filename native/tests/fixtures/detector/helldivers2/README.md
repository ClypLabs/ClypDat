# Helldivers 2 counter replay

`counter-000.png` through `counter-059.png` sample the supplied
`Killstreak ×50 - Sep-14-2026 - 12-38-01.mp4` recording at 2 fps, starting at 0s.
Each image contains only the 308×174 HUD slot at (1152, 1036) in the original
2560×1440 recording. These are test inputs; they are not shipped with the app.

Expected result: one `killstreak-50` event, label `Killstreak ×57`, disappearance
at 28s, confirmed at 29s. Earlier threshold crossings produce no events.
The sequence includes unreadable OCR frames, the smaller early skull, and fade.

Extraction:

```powershell
ffmpeg -i <recording.mp4> -t 30 -vf 'fps=2,crop=308:174:1152:1036:exact=1' -start_number 0 counter-%03d.png
```

The production skull template in `native/detector-templates/helldivers2/skull.png`
is the 48×48 crop at (30, 28) from the user's separately supplied HUD image.
