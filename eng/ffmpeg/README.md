# Controlled FFmpeg shared build

Build FFmpeg 8.1.2 and its locked dependencies into a fresh staging directory:

```powershell
python eng/ffmpeg/bootstrap.py D:/ClypDat-builds/ffmpeg-8.1.2-shared-r1
```

Read [BUILD.md](BUILD.md) for exact Windows toolchain prerequisites, source hashes, oneVPL configuration, output files and reproducibility limits. [LICENSE-REVIEW.md](LICENSE-REVIEW.md) records redistribution obligations.

This standalone recipe does not update the production SDK pin or `native/vendor/ffmpeg`. Generated packages require feature, ABI, hardware and media acceptance before integration. Publish corresponding sources and notices with any released binary package.
