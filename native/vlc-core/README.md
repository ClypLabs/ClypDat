# Patched libvlccore

ClypDat ships its own build of `libvlccore.dll` in place of the one in the
`VideoLAN.LibVLC.Windows` 3.0.23.1 package. Every plugin and `libvlc.dll` still
come from the package.

- Source: VLC 3.0.23 release tarball, revision `3.0.23-2-0-g79128878dd`, the
  same revision the package was built from.
- Changes: `patches/` (applied in order).
- Binary: `prebuilt/libvlccore.dll`, built by `build-libvlccore.sh` in an MSYS2
  MINGW64 shell. Its SHA-256 is pinned in `NativeVideoOutput.CoreSha256`.
- Exports: identical to the package's (764 symbols). Imports: the same system
  DLLs and `msvcrt.dll`, plus `IPHLPAPI.DLL`.

## Patches

`0001-decoder-flush-cancels-pending-drain.patch`: a seek in a clip's last
second lets the demuxer read ahead to end of file, which queues a decoder drain.
VLC 3.0's flush on the next seek left that drain pending; it ran once the
post-seek blocks were decoded, discarded the codec's reference pictures, and
every following P-frame decoded grey or ghosted until the end of the clip.
`EditorEndSeekTests` reproduces it (red on the package's libvlccore, green on
this one).

## Rebuilding

```sh
# MSYS2 MINGW64 shell
pacman -S mingw-w64-x86_64-gcc mingw-w64-x86_64-pkgconf make autoconf automake libtool tar xz patch
./build-libvlccore.sh
```

Then update `CoreSha256` in `NativeVideoOutput.cs` to the printed hash.

libvlccore is LGPL-2.1-or-later; the patches here and the public VLC 3.0.23
source are its corresponding source.
