#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
project="${1:?usage: assemble-phase5-unity.sh <unity-project-path>}"
assets="$project/Assets"
dest="$assets/UnityLlmTransport"

rm -rf "$dest"
mkdir -p "$dest/Abstractions" "$dest/WebSocket"
mkdir -p "$assets/Scripts" "$assets/Editor"

copy_cs() {
  local src="$1" dst="$2"
  find "$src" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print0 |
  while IFS= read -r -d '' file; do
    local rel="${file#"$src"/}"
    mkdir -p "$dst/$(dirname "$rel")"
    cp "$file" "$dst/$rel"
  done
}

copy_cs "$repo/src/UnityLlmTransport.Abstractions" "$dest/Abstractions"
copy_cs "$repo/src/UnityLlmTransport.WebSocket" "$dest/WebSocket"
rm -f "$dest/WebSocket/Internal/IsExternalInit.cs"

cp "$repo/unity/Phase5Harness/Assets/Scripts/Phase5Runner.cs" "$assets/Scripts/"
cp "$repo/unity/Phase5Harness/Assets/Editor/Phase5Build.cs" "$assets/Editor/"

echo "assembled Phase 5 assets -> $project"
