# Controlled FFmpeg shared build

Build FFmpeg 8.1.2 and its locked dependencies into a fresh staging directory:

```powershell
python eng/ffmpeg/bootstrap.py D:/ClypDat-builds/ffmpeg-8.1.2-shared-r2
```

Read [BUILD.md](BUILD.md) for exact Windows toolchain prerequisites, source hashes, oneVPL configuration, output files and deterministic-build controls and acceptance gates. [LICENSE-REVIEW.md](LICENSE-REVIEW.md) records redistribution obligations.

The accepted r2 binary and matching source ZIPs are pinned in `artifacts/`. `eng/Prepare-CaptureFfmpegSdk.ps1` extracts matching headers and import libraries from the binary ZIP; `native/vendor/ffmpeg/runtime-manifest.json` pins its runtime payload. `eng/Verify-CaptureFfmpegRuntime.ps1` checks vendor, build and publish outputs. Release workflows attach the matching source ZIP beside installers. A freshly generated package requires the same feature, ABI, hardware and media acceptance before changing these production pins.
