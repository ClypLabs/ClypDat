# Candidate redistribution review

This review applies to the candidate's explicit configure command, not to a generic FFmpeg build. Final executable license output and enabled-library audit must agree before acceptance.

| Component | Source / configuration | License and distribution requirements |
|---|---|---|
| FFmpeg 8.1.2 | requested commit; `--enable-gpl --enable-version3`, no `--enable-nonfree` | GPL-3.0-or-later, matching the existing Gyan packages and GPLv3 ClypDat. Distribute corresponding source, exact build instructions, license texts and notices with accessible matching source distribution. |
| x264 | pinned commit in source lock; static codec library inside avcodec | GPL-2.0-or-later. Include full source, COPYING and build instructions; combined FFmpeg remains GPLv3-or-later. |
| x265 | upstream 4.1 tag, commit `1d117bed4747758b51bd2c124d738527e30392cb`; static codec library | GPL-2.0-or-later; same source/notice obligations. Upstream archive's x265Version.txt reports stale `4.0+1-6318f22`; tag/commit identity, not that generated banner, establishes source provenance. |
| libaom | v3.13.1 commit `d772e334cc724105040382a977ebb10dfd393293`; static codec library | BSD-2-Clause and AOM Patent License 1.0; include LICENSE, PATENTS and relevant third-party notices. |
| dav1d | 1.5.4 commit `54706fc6bc0cdecab7e9593974a4039cc038fca7`; static AV1 decoder | BSD-2-Clause; preserve COPYING and per-file notices. No change to GPLv3 license mode. |
| oneVPL | v2.16.0 commit `778a66d6c6537f08eabb91955dbbf1bce3812894`; single shared libvpl.dll, BUILD_EXPERIMENTAL=ON | MIT; include copyright/permission notice and source. Intel driver runtime is discovered on the user's machine, not redistributed. |
| NV codec headers | n13.0.19.0 | MIT-style NVIDIA header copyright/permission text. No NVIDIA SDK or driver binaries bundled. |
| AMF headers | v1.4.36 commit `16f7d73e0b45c473e903e46981ed0b91efc4c091` (annotated tag object `86b1b09ca5c0572ab710ee7b6b174f8c7aa00119`) | MIT and AMD standards notice; retain LICENSE.txt and documented C compatibility header patch. No AMD driver runtime bundled. |
| zlib | 1.3.1 | zlib license; retain zlib.h notice, source and documented MSVC header-guard patch. Static inside FFmpeg; no zlib DLL needed. |
| Microsoft C/C++ runtime | release x64 DLLs from installed VC/Redist/MSVC/14.51.36231 | Microsoft redistributable terms, separate from GPL component licenses. Preserve binaries unchanged, include provenance/terms links. Only release redistribution files, never debug or preview files. |
| FFmpeg IJG-derived routines | unmodified jfdctfst.c, jfdctint_template.c, jrevdct.c | Include the required IJG acknowledgement and original notices. Source archive retains all per-file licenses. |

FFmpeg and required software encoder licenses match the existing GPLv3 distribution model. No unnecessary nonfree codec/library enabled. oneVPL adds an MIT notice; Microsoft release runtime redistribution is a packaging obligation. Tooling such as NASM, Make, pkgconf, CMake and Git is not shipped in the runtime package; source lock and tool inventory record the build prerequisites.

Microsoft permits unmodified files under `VC/redist` with a validly licensed Visual Studio distribution, excluding debug and preview components. This is a release packaging check, not permission to change the user's system runtime. The package uses application-local runtime DLLs; no installer or registry change is performed. [Visual Studio 2026 distributable files](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution), [Microsoft redistribution guidance](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170).

The existing RNNoise model is neither changed nor added by this dependency build. Its existing third-party notice remains applicable to ClypDat; `arnndn` is a built-in FFmpeg filter.

The AMF download also contains unrelated prebuilt FFmpeg 7 libraries. They are excluded from runtime and corresponding-source deliverables. Only used AMF headers, the C compatibility patch and AMD notice are included. The full upstream archive is acquisition evidence, not part of the proposed release.

For release, publish the exact corresponding source bundle beside the binary package; keep these build instructions and license notices available. A local validated candidate is not itself a publication. [FFmpeg licensing](https://ffmpeg.org/legal.html), [oneVPL license](https://github.com/intel/libvpl/blob/v2.16.0/LICENSE), [libaom license](https://aomedia.googlesource.com/aom/+/refs/tags/v3.13.1/LICENSE).

## AOM-only compiler in revision r2

LLVM clang-cl 22.1.8, commit `ca7933e47d3a3451d81e72ac174dcb5aa28b59d1`, is a build tool only. Its official Windows archive and exact compiler executable are hash-pinned. The installer is extracted, not executed. AOM still links against the Microsoft release CRT; no LLVM DLL or compiler binary is included in the runtime/source package.

LLVM uses Apache-2.0 WITH LLVM-exception. The compilation exception covers portions embedded in object code as a result of compilation; this introduces no new copyleft mode into the GPLv3-or-later FFmpeg package. Preserve the pinned complete LLVM license in `licenses/LLVM-LICENSE.TXT` and the corresponding-source downloads. Build instructions/locks identify the immutable tool download and source commit. [LLVM license at the pinned commit](https://github.com/llvm/llvm-project/blob/ca7933e47d3a3451d81e72ac174dcb5aa28b59d1/llvm/LICENSE.TXT), [LLVM licensing policy](https://llvm.org/docs/DeveloperPolicy.html#license).

The large compiler installer is not bundled with application or corresponding-source ZIPs. It remains an independently downloadable, hash-verified build prerequisite, like the existing Microsoft compiler. Matching FFmpeg and codec sources, patches, recipe and notices remain included. Production dependency/source-release integration is outside this candidate recipe change.
