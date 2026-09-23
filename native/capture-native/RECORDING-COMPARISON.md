# Five-round generated recording comparison

Both paths recorded the same generated 128x72 BGRA frames and 44.1 kHz stereo
tone. Each run saved a three-second CPU libx264 clip. The eight cases cover
30, 60, 90 and 120 fps in CFR and VFR, with five runs per case per backend.
All 80 clips decoded every stream and passed a seek check. Native health
reported zero replaced history frames. Runs did not open screen, camera,
microphone or loopback devices.

CPU includes full media decode. The managed decoder ran as a child FFmpeg
process, so its process CPU is added to the managed recorder CPU. Save time
ends when the file is published and excludes the later decode check. Working
set and private bytes were sampled at completion. P95 uses nearest-rank on
five samples; in this set P95 equals the sample maximum.

| FPS | Mode | CPU ms, managed median / P95 | CPU ms, native median / P95 | Save ms, managed median / P95 | Save ms, native median / P95 | Working set MiB, managed / native median |
| ---: | :--- | ---: | ---: | ---: | ---: | ---: |
| 30 | CFR | 46.88 / 281.25 | 62.50 / 234.38 | 230.05 / 259.10 | 179.44 / 194.28 | 176.7 / 160.8 |
| 30 | VFR | 93.75 / 328.12 | 62.50 / 93.75 | 211.66 / 220.69 | 174.23 / 199.63 | 174.8 / 160.3 |
| 60 | CFR | 171.88 / 203.12 | 46.88 / 234.38 | 218.90 / 231.09 | 179.99 / 180.46 | 175.8 / 160.6 |
| 60 | VFR | 109.38 / 171.88 | 109.38 / 171.88 | 212.85 / 225.97 | 179.66 / 183.14 | 175.6 / 160.6 |
| 90 | CFR | 171.88 / 203.12 | 78.12 / 171.88 | 208.99 / 220.94 | 186.27 / 186.71 | 176.0 / 160.9 |
| 90 | VFR | 218.75 / 312.50 | 62.50 / 109.38 | 207.52 / 219.44 | 183.79 / 194.16 | 175.0 / 160.9 |
| 120 | CFR | 140.62 / 250.00 | 218.75 / 468.75 | 213.95 / 244.06 | 181.23 / 199.94 | 175.2 / 161.1 |
| 120 | VFR | 125.00 / 218.75 | 62.50 / 171.88 | 214.09 / 225.54 | 184.32 / 192.01 | 175.3 / 160.7 |

Median native save time was 11–22% lower. Median working set was 8–9% lower.
CPU results varied by case: native median was 33% higher at 30 fps CFR and
56% higher at 120 fps CFR. At 120 fps CFR, native P95 was 469 ms versus 250 ms
for managed. Five runs do not establish a stable CPU regression, but the
120 fps CFR case needs profiling before claiming CPU parity.

Native health encoded 90 and 180 frames at 30 and 60 fps. At 90 fps it encoded
270 or 271; at 120 fps it encoded 360 or 361. Timestamp scheduling can include
one boundary frame at the two higher rates. Those clips decoded and sought;
the small excess does not count as a frame replacement.

Raw clips and JSON reports from this run remain in the ignored workspace
folders `.local/cpp-recording-comparison-managed-five/` and
`.local/cpp-recording-comparison-native-five/`. This generated-frame test
does not measure the cost of live WGC/DXGI acquisition, camera capture or
physical WASAPI device replacement. The RTX 4070 Ti NVENC tests use generated
textures; AMD and Intel encoder hardware were not available for validation.
