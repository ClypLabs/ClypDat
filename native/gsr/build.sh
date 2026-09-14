#!/usr/bin/env bash
set -euo pipefail
script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_root="${CLYPDAT_GSR_SOURCE:-$script_root/build/source}"
pinned_commit=af736d4c38789f6c6f4b2236b9ac2a03c186703d
for tool in git meson ninja pkg-config wayland-scanner; do
    command -v "$tool" >/dev/null || { echo "Missing build dependency: $tool" >&2; exit 1; }
done
if [[ ! -d "$source_root/.git" ]]; then
    git clone --no-checkout https://repo.dec05eba.com/gpu-screen-recorder "$source_root"
    git -C "$source_root" checkout --detach "$pinned_commit"
    cp "$script_root/protocols/plasma-window-management.xml" "$source_root/protocol/"
    git -C "$source_root" apply "$script_root/patches/0001-kde-window-metadata.patch"
fi
[[ "$(git -C "$source_root" rev-parse HEAD)" == "$pinned_commit" ]] || {
    echo 'GSR source does not match the pinned commit.' >&2; exit 1;
}
git -C "$source_root" apply --reverse --check "$script_root/patches/0001-kde-window-metadata.patch"
cmp "$script_root/protocols/plasma-window-management.xml" "$source_root/protocol/plasma-window-management.xml"
meson setup "$source_root/build" "$source_root" --reconfigure \
    -Dsystemd=false -Dcapabilities=false -Dnvidia_suspend_fix=false
meson compile -C "$source_root/build"
"$source_root/build/gpu-screen-recorder" --clypdat-capabilities
