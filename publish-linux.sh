#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"
[[ "$(uname -s)/$(uname -m)" == Linux/x86_64 ]] || { echo 'Linux x86_64 is required.' >&2; exit 1; }
output="$repo_root/.local/linux-x64"
avalonia="$repo_root/../clypdat-avalonia"
expected_avalonia="$(python3 -c 'import xml.etree.ElementTree as E; print(E.parse("eng/AvaloniaPin.props").findtext(".//ClypDatAvaloniaLinuxCommit"))')"
[[ "$(git -C "$avalonia" rev-parse HEAD)" == "$expected_avalonia" ]] || {
    echo "Avalonia checkout must match Linux pin $expected_avalonia" >&2; exit 1;
}
"$repo_root/eng/dotnet-linux.sh" msbuild "$avalonia/build/ClypDat.LinuxPackages.proj" /t:Pack
"$repo_root/native/gsr/build.sh"
source_root="${CLYPDAT_GSR_SOURCE:-$repo_root/native/gsr/build/source}"
"$repo_root/eng/dotnet-linux.sh" publish native/src/ClypDat.App/ClypDat.App.csproj \
    -p:ClypDatLinux=true -r linux-x64 -c Release --self-contained true -o "$output"
mkdir -p "$output/libexec" "$output/licenses/gsr" "$output/source/gsr"
cp "$source_root/build/gpu-screen-recorder" "$output/libexec/clypdat-gsr"
cp native/gsr/COPYING native/gsr/UPSTREAM.md "$output/licenses/gsr/"
cp -R native/gsr/patches native/gsr/protocols native/gsr/tests "$output/source/gsr/"
cp native/gsr/build.sh native/gsr/UPSTREAM.md "$output/source/gsr/"
git -C "$source_root" archive --format=tar.gz HEAD > "$output/source/gsr/upstream-6.1.2.tar.gz"
cp assets/clypdat-icon.png "$output/clypdat-icon.png"
cp native/linux/install-desktop.py "$output/install-desktop.py"
cp docs/linux-support.md "$output/LINUX-STATUS.md"
"$output/ClypDat" --verify-self-contained
"$output/ClypDatRecorder" --verify-self-contained
"$output/libexec/clypdat-gsr" --clypdat-capabilities
printf 'Experimental build: %s\nRegister desktop entries: python3 "%s/install-desktop.py"\n' "$output" "$output"
