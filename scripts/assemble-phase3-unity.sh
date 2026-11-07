#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
project="${1:?usage: assemble-phase3-unity.sh <unity-project-path>}"
assets="$project/Assets"
dest="$assets/UnityLlmTransport"

rm -rf "$dest"
mkdir -p "$dest/Abstractions" "$dest/Sse" "$dest/HttpClient"
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
copy_cs "$repo/src/UnityLlmTransport.Sse" "$dest/Sse"
rm -f "$dest/Sse/Internal/IsExternalInit.cs"
copy_cs "$repo/src/UnityLlmTransport.HttpClient" "$dest/HttpClient"
rm -f "$dest/HttpClient/Internal/IsExternalInit.cs"

cp "$repo/unity/Phase3Harness/Assets/Scripts/Phase3Runner.cs" "$assets/Scripts/"
cp "$repo/unity/Phase3Harness/Assets/Editor/Phase3Build.cs" "$assets/Editor/"

echo "assembled Phase 3 assets -> $project"
