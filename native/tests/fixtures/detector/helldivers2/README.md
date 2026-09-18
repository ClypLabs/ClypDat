# Helldivers 2 counter replay

`counter-000.png` through `counter-059.png` sample the supplied
`Killstreak ×50 - Sep-14-2026 - 12-38-01.mp4` recording at 2 fps, starting at 0s.
Each image contains only the 308×174 HUD slot at (1152, 1036) in the original
2560×1440 recording. These are test inputs; they are not shipped with the app.

Expected result: one `killstreak` event, label `Killstreak ×57`, disappearance
at 28s, confirmed at 29s. Earlier threshold crossings produce no events.
The sequence includes unreadable OCR frames, the smaller early skull, and fade.

Extraction:

```powershell
ffmpeg -i <recording.mp4> -t 30 -vf 'fps=2,crop=308:174:1152:1036:exact=1' -start_number 0 counter-%03d.png
```

The production skull template in `native/detector-templates/helldivers2/skull.png`
is the 48×48 crop at (30, 28) from the user's separately supplied HUD image.

The `replay-*` directories use the same extraction and crop, sampled through
the end of each supplied recording:

| Directory | Recording | Expected result |
| --- | --- | --- |
| `replay-110` | `HELLDIVERS™ 2 - Sep-19-2026 - 01-56-12.mp4` | One `Killstreak ×110`, disappearance at 40.5s, confirmation at 41.5s |
| `replay-77` | `Killstreak ×77 - Sep-19-2026 - 01-55-47.mp4` | No completion before the file ends; counter passes 100 |
| `replay-45` | `Killstreak ×45 - Sep-18-2026 - 18-59-37.mp4` | No completion before the file ends; counter passes 45 |
| `replay-58` | `Killstreak ×58 - Sep-18-2026 - 18-59-51.mp4` | One `Killstreak ×58`, disappearance at 23s, confirmation at 24s |

These colour crops exercise the same mask predicates as live NV12 capture.
Tests assert exact labels, no extra completions, and the unchanged one-second
absence confirmation. The original ×57 replay also covers the gold skull at
lower counts and the dim pink skull during fade at each supported crop size.
Fixture-runner positive labels use `expectedLabel` to distinguish exact counts;
an empty `labels` array requires no events throughout the recording.
