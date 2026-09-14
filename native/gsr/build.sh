#!/usr/bin/env bash
set -euo pipefail
script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_root="${CLYPDAT_GSR_SOURCE:-$script_root/build/source}"
pinned_commit=af736d4c38789f6c6f4b2236b9ac2a03c186703d
# Build tools live in a versioned cache, never in a previous agent's /tmp.
build_tools="${XDG_CACHE_HOME:-$HOME/.cache}/clypdat/gsr-tools-1.10.2"
if ! command -v meson >/dev/null || ! command -v ninja >/dev/null; then
    if [[ ! -x "$build_tools/bin/meson" || ! -x "$build_tools/bin/ninja" ]]; then
        python3 -m venv "$build_tools"
        "$build_tools/bin/python" -m pip install 'meson==1.10.2' 'ninja==1.13.0'
    fi
    export PATH="$build_tools/bin:$PATH"
fi
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
cp "$script_root"/extension/* "$source_root/src/"
cp "$script_root"/protocols/*.xml "$source_root/protocol/"
git -C "$source_root" apply --reverse --check "$script_root/patches/0001-kde-window-metadata.patch"
cmp "$script_root/protocols/plasma-window-management.xml" "$source_root/protocol/plasma-window-management.xml"
vulkan_root="${XDG_CACHE_HOME:-$HOME/.cache}/clypdat/vulkan-headers-ee2ec5f"
if [[ ! -f /usr/include/vulkan/vulkan.h ]]; then
    if [[ ! -d "$vulkan_root/.git" ]]; then
        git clone https://github.com/KhronosGroup/Vulkan-Headers.git "$vulkan_root"
        git -C "$vulkan_root" checkout --detach ee2ec5fd83dafce291024683b50dc89219333076
    fi
    [[ "$(git -C "$vulkan_root" rev-parse HEAD)" == ee2ec5fd83dafce291024683b50dc89219333076 ]]
    export C_INCLUDE_PATH="$vulkan_root/include${C_INCLUDE_PATH:+:$C_INCLUDE_PATH}"
fi
meson setup "$source_root/build" "$source_root" --reconfigure \
    -Dsystemd=false -Dcapabilities=false -Dnvidia_suspend_fix=false
meson compile -C "$source_root/build"
"$source_root/build/gpu-screen-recorder" --clypdat-capabilities
