#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$repo_root/native/src/ClypDat.App/ClypDat.App.csproj"
preview_root="$repo_root/.local/macos-ui-preview"
publish_dir="$preview_root/publish"
app_bundle="$preview_root/ClypDat.app"
app_contents="$app_bundle/Contents"
app_binary="$app_contents/MacOS/ClypDat"

target="${1:-local}"
if [[ "$target" != "local" ]]; then
    echo "Only the local worktree is supported by the macOS UI preview." >&2
    echo "Usage: ./publish-macos.sh [local]" >&2
    exit 2
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet was not found on PATH. Install the .NET 10 SDK, then rerun." >&2
    exit 1
fi

case "$(uname -m)" in
    arm64|aarch64) runtime_id="osx-arm64" ;;
    x86_64|amd64) runtime_id="osx-x64" ;;
    *)
        echo "Unsupported macOS architecture: $(uname -m)" >&2
        exit 1
        ;;
esac

if [[ ! -f "$project" ]]; then
    echo "ClypDat project not found: $project" >&2
    exit 1
fi

mkdir -p "$preview_root"
rm -rf "$publish_dir" "$app_bundle"

echo "Publishing ClypDat UI preview for $runtime_id."
dotnet publish "$project" \
    --configuration Release \
    --runtime "$runtime_id" \
    --self-contained true \
    --output "$publish_dir" \
    -p:ClypDatUiPreview=true \
    -p:PublishReadyToRun=false

mkdir -p "$app_contents/MacOS" "$app_contents/Resources"
cp -R "$publish_dir/." "$app_contents/MacOS/"
cp "$repo_root/native/macos/Info.plist" "$app_contents/Info.plist"

if [[ -x "$app_binary" ]]; then
    pkill -f "$app_binary" 2>/dev/null || true
fi

echo "Starting $app_bundle"
open "$app_bundle"
