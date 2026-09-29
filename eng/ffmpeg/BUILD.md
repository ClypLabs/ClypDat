# ClypDat FFmpeg 8.1.2 shared candidate

This recipe builds a separate candidate. It does not call `Prepare-CaptureFfmpegSdk.ps1`, write `native/vendor/ffmpeg`, modify recorder code, install drivers, change registry settings, or change system PATH.

## Identity and dependencies

- FFmpeg n8.1.2 source commit: `38b88335f99e76ed89ff3c93f877fdefce736c13`. Upstream annotated tag object: `1c2c67c0b9f7f66ab32c19dcf7f227bcd290aa4c`.
- oneVPL headers and dispatcher: 2.16.0, commit `778a66d6c6537f08eabb91955dbbf1bce3812894`.
- oneVPL configuration: `BUILD_SHARED_LIBS=ON`, `BUILD_EXPERIMENTAL=ON`. FFmpeg receives `ONEVPL_EXPERIMENTAL=1` globally. Both avutil and avcodec link the **same absolute import library** `deps/lib/vpl.lib`, whose target is `libvpl.dll`. No static dispatcher archive is produced or linked.
- Required external codecs: x264 pinned snapshot, x265 4.1 tag (8/10/12-bit multilib), libaom 3.13.1. Static codec libraries using the Microsoft `/MD` runtime. AOM alone uses pinned clang-cl 22.1.8; FFmpeg and other C/C++ dependencies retain MSVC.
- Default AV1 decoding: dav1d 1.5.4, commit `54706fc6bc0cdecab7e9593974a4039cc038fca7`, built with pinned Meson 1.9.1 and `b_vscrt=md`. This preserves the baseline's automatically selected AV1 decoder; libaom also remains available for CPU AV1 export.
- GPU headers: nv-codec-headers n13.0.19.0 and AMF v1.4.36. GPU driver runtimes are not bundled.
- `patches/amf-display-c-compat.patch` makes the AMF 1.4.36 display-capture declaration valid in C as well as C++. It preserves the AMF version and ABI; no encoder logic changes. The original tag object is `86b1b09ca5c0572ab710ee7b6b174f8c7aa00119`, resolving to commit `16f7d73e0b45c473e903e46981ed0b91efc4c091`.
- zlib 1.3.1, static. `patches/zlib-msvc-unistd.patch` fixes a header conditional: FFmpeg defines `HAVE_UNISTD_H=0`, while zlib 1.3.1 tests only whether the macro exists. The recipe applies this exact Windows guard to the CMake header template before compilation. No FFmpeg or oneVPL source patches.
- Complete immutable revisions, download locations and SHA-256 values: `sources.lock.json`.

x265 is compiled in separate 8-, 10- and 12-bit namespaces, with the public C API owned by the 8-bit build and `LINKED_10BIT=ON` / `LINKED_12BIT=ON`. MSVC `lib.exe` combines all three static archives. This preserves automatic high-bit-depth CPU export. The layout follows the [upstream multilib recipe](https://github.com/videolan/x265/blob/master/build/msys/multilib.sh); the exact x265 4.1 CMake files define these switches.

The x265 4.1 source archive contains stale version metadata (`4.0+1-6318f22`). Its upstream tag resolves to `1d117bed4747758b51bd2c124d738527e30392cb`, the tree being built. Preserve this distinction when reading encoder banners.

## Build prerequisites

The recorded machine uses Windows 11 x64, Visual Studio Community 2026 18.9.1 at `C:/Program Files/Microsoft Visual Studio/18/Community`, MSVC tools directory 14.51.36231 with compiler 19.51.36256.0, Windows SDK 10.0.26100.0, VS CMake 4.3.1-msvc1 and bundled Ninja. Install matching released toolchain components before reproducing; the script does not install them. `toolchain.lock.json` records compiler frontends/backend, the AOM-only clang-cl compiler, linker/resource compiler, CMake, Ninja, Git, Bash, 7-Zip and Python SHA-256 values. Build startup checks these hashes and selected MSVC/SDK versions; drift fails closed. Python may live at a different path if its executable hash matches.

Other prerequisites: Git for Windows 2.55.0.windows.3 at `C:/Program Files/Git`, Python 3.13, 7-Zip at `C:/Program Files/7-Zip/7z.exe`, and a D: drive. LLVM 22.1.8 (AOM compiler only), CMake 3.31.8 (x265 only), NASM 2.16.03, GNU Make 4.4.1-3 and pkgconf 3.0.7-1 are acquired from locked archives into staging. The LLVM installer is hash-checked and extracted with 7-Zip, never executed. Only clang-cl and its resource headers are extracted; no global installation or LLVM runtime DLL is added. The older CMake is needed for upstream x265's OLD policy settings; no CMake source patch is used.

## Fresh reproduction

From the directory containing this recipe:

```powershell
python bootstrap.py 'D:/ClypDat-builds/ffmpeg-8.1.2-shared-r2'
```

The target must not exist. The fixed build junction `D:/ClypDatFfmpeg812` must also be free. The bootstrap downloads and verifies every locked source/tool archive, extracts into the target, creates that junction, builds dependencies and FFmpeg, installs only into `candidate/`, assembles notices and runtime files, and creates binary/source ZIPs with manifests. Download hash mismatches fail closed.

libaom's upstream archive endpoint returned HTTP 503 during initial acquisition. Reproduction clones the named upstream tag, requires its HEAD to equal the locked commit, creates a `git archive --format=tar.gz`, and verifies the archived SHA-256 before building. Git source is used for that dependency so its version generation matches this build. The resulting source archive is included in the corresponding-source package.

The AMF upstream archive includes unrelated prebuilt FFmpeg 7 libraries under `Thirdparty`. They are never used or copied into the candidate. The corresponding-source ZIP contains only the AMF headers actually used and AMD's notice; the lock retains the full upstream acquisition hash. Both header patches are included in the recipe and source package.

The fixed path avoids MSYS quoting problems and stabilizes embedded configure paths. All PATH/TEMP changes apply only to child build processes. Build temporary files remain under the staging directory. The junction remains available for investigation after the script exits; remove only the junction after builds/tests finish, preserving its target.

## Configure and build

`configure-command.json` holds the exact argument array. `build-candidate.py` is the executable recipe. It enables full default built-in codecs, formats, filters and Windows devices, plus required external libraries. It does not use `--disable-everything`. `--disable-autodetect` prevents unpinned external dependencies from entering the build.

FFmpeg uses shared DLLs, MSVC x64, native Windows threads, NASM optimization, `/MD`, GPLv3 mode, QSV/D3D11VA/DXVA2, NVENC/NVDEC/CUVID, AMF, x264, x265, libaom, dav1d, zlib and SChannel. `make REVISION=8.1.2` fixes release identity for an archive source tree. All dependency headers/import libraries come from the same staging prefix.

For an interrupted build in this exact existing staging directory:

```powershell
python build-candidate.py deps
python build-candidate.py ffmpeg
# Only after configure has succeeded and flags/sources are unchanged:
python build-candidate.py ffmpeg --resume
python package.py
```

`--from=aom` and `--from=x264` on the dependency command are development resume options, not fresh-build requirements. Never use them when the preceding verified dependencies are absent.

## Outputs and reproducibility scope

- `candidate/bin`: ffmpeg.exe, ffprobe.exe, FFmpeg shared libraries, one libvpl.dll and the required unmodified Microsoft release runtime DLLs.
- `candidate/include`, `candidate/lib`: matching public development headers and import libraries. Shared-only pkg-config metadata uses a relocatable prefix; build-machine static dependency paths are removed.
- `candidate/licenses`, `candidate/BUILD.md`, source/configuration locks and dependency patches: redistribution material.
- `candidate/SHA256SUMS`, `candidate/manifest.json`: final file hashes.
- `clypdat-ffmpeg-8.1.2-win64-shared-r2.zip` and matching `.sha256`.
- `clypdat-ffmpeg-8.1.2-win64-shared-r2-sources.zip` and matching `.sha256`: corresponding sources and recipe. Publish beside the binaries if released.

Revision r2 addresses two independently reproduced defects in r1:

- MSVC 19.51.36256 produces two `parse_decode_block` instruction sequences from identical preprocessed AOM input, command, environment and output path. A fixed-command serial trial produced 28/2 variants in 30 compilations. `/Brepro` and `/experimental:deterministic` still produced both variants. The difference exists in `decodeframe.obj` before archive/link time; no `/GL`, LTCG or PGO is involved. Identical preprocessing and reproduction without parallelism exclude a generated-header/build-order race. A reduced standalone translation unit retains both original function hashes. The internal optimizer defect is not known.
- Default MSVC linking writes wall-clock COFF/debug timestamps. Controlled repeated links of identical input become identical with `/Brepro`. FFmpeg and oneVPL shared linking use that option. AOM's clang-cl compiler and MSVC librarian use `/Brepro`; NASM uses `--reproducible` through `CMAKE_ASM_NASM_FLAGS`. Experiments verified that these eliminate actual object/archive metadata differences. No binary bytes are rewritten after compilation/linking.

AOM retains `/O2`, `/Ob2`, `/MD`, assembly and the same codec features. Its compiler workaround is deliberately limited to this dependency. LLVM's immutable release archive, compiler executable and license are pinned in the locks. No alternate linker, compiler runtime or system installation is introduced.

Acceptance requires two entirely new build roots, independently verified downloads/toolchains and no reused compiled dependencies. Compare every candidate file, `manifest.json`, `SHA256SUMS`, binary ZIP and source ZIP by SHA-256. ZIP ordering and timestamps are fixed. Leave build A untouched, remove only its fixed-path junction, then bootstrap build B. A successful single build is not proof of reproducibility. Any different payload or ZIP hash blocks acceptance; hardware/software smoke and CPU AV1 performance comparison remain separate gates. Never substitute old validated binaries to make hashes match.

## Acceptance and integration boundary

Native tests link against the existing ClypDat SDK headers/import libraries and load candidate DLLs from isolated test directories. Direct hardware-frame tests, frame/adapter inspection, keyframe cadence, saved replay decode, media CLI workflows, ABI export checks and feature diffs are separate gates. Do not treat a successful build as acceptance.

The existing shared ABI names are avcodec-62, avformat-62, avutil-60, swresample-6 and swscale-9. No ClypDat recorder API adaptation is part of this recipe. Production pin changes require reviewing the acceptance report and exact package hashes first.
