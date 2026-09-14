#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sdk_version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "$repo_root/global.json")"
sdk_root="${XDG_CACHE_HOME:-$HOME/.cache}/clypdat/dotnet-linux"
if [[ ! -x "$sdk_root/dotnet" || ! -d "$sdk_root/sdk/$sdk_version" ]]; then
    installer="$(mktemp)"
    trap 'rm -f "$installer"' EXIT
    curl --fail --location --silent --show-error https://dot.net/v1/dotnet-install.sh -o "$installer"
    bash "$installer" --version "$sdk_version" --architecture x64 --install-dir "$sdk_root" --no-path
fi
export DOTNET_ROOT="$sdk_root"
export PATH="$sdk_root:$PATH"
exec "$sdk_root/dotnet" "$@"
