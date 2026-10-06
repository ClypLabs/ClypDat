#!/usr/bin/env bash
# Rebuilds prebuilt/libvlccore.dll: VLC 3.0.23 (the exact revision the pinned
# VideoLAN.LibVLC.Windows 3.0.23.1 package ships, 3.0.23-2-0-g79128878dd) with
# the patches in patches/ applied. Run from an MSYS2 MINGW64 shell with
#   pacman -S mingw-w64-x86_64-gcc mingw-w64-x86_64-pkgconf make autoconf automake libtool tar xz patch
# Only libvlccore is replaced; every plugin still comes from the package.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
work="${1:-${TMPDIR:-/tmp}/clypdat-vlccore}"
version=3.0.23
sha256=e891cae6aa3ccda69bf94173d5105cbc55c7a7d9b1d21b9b21666e69eff3e7e0

mkdir -p "$work"
cd "$work"
[ -f "vlc-$version.tar.xz" ] || curl -sSLO "https://download.videolan.org/pub/videolan/vlc/$version/vlc-$version.tar.xz"
echo "$sha256 *vlc-$version.tar.xz" | sha256sum -c -
rm -rf "vlc-$version"
tar xf "vlc-$version.tar.xz"
cd "vlc-$version"
grep -q 'g79128878dd' src/revision.txt
for p in "$here"/patches/*.patch; do patch -p1 < "$p"; done

BUILDCC=x86_64-w64-mingw32-gcc ./configure --host=x86_64-w64-mingw32 --build=x86_64-w64-mingw32 \
  --disable-nls --disable-lua --disable-qt --disable-skins2 --disable-vlc --disable-a52 \
  --disable-libgcrypt --disable-update-check --disable-avcodec --disable-swscale --disable-postproc \
  --disable-chromecast --disable-mad --disable-taglib --disable-libxml2 --disable-dbus --disable-vlm \
  --disable-addonmanagermodules
make -j"$(nproc)" -C compat
make -j"$(nproc)" -C src
mkdir -p "$here/prebuilt"
cp src/.libs/libvlccore.dll "$here/prebuilt/libvlccore.dll"
x86_64-w64-mingw32-strip "$here/prebuilt/libvlccore.dll"
sha256sum "$here/prebuilt/libvlccore.dll"
