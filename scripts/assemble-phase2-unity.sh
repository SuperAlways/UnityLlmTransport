#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
project="${1:?usage: assemble-phase2-unity.sh <unity-project-path>}"
assets="$project/Assets"
dest="$assets/UnityLlmTransport"

rm -rf "$dest"
mkdir -p "$dest/Abstractions" "$dest/Sse" "$dest/Core" "$dest/UnityWebRequest" "$dest/Hosts"
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
copy_cs "$repo/src/UnityLlmTransport.UnityWebRequest.Core" "$dest/Core"
rm -f "$dest/Core/Internal/IsExternalInit.cs"

if [ -d "$repo/unity/UnityLlmTransport.UnityWebRequest/Runtime" ]; then
  copy_cs "$repo/unity/UnityLlmTransport.UnityWebRequest/Runtime" "$dest/UnityWebRequest"
  cp "$repo/unity/UnityLlmTransport.UnityWebRequest/link.xml" "$assets/link.xml"
fi
if [ -d "$repo/unity/UnityLlmTransport.Hosts.Unity/Runtime" ]; then
  copy_cs "$repo/unity/UnityLlmTransport.Hosts.Unity/Runtime" "$dest/Hosts"
fi
if [ -f "$repo/unity/Phase2Harness/Assets/Scripts/Phase2Runner.cs" ]; then
  cp "$repo/unity/Phase2Harness/Assets/Scripts/Phase2Runner.cs" "$assets/Scripts/"
fi
if [ -f "$repo/unity/Phase2Harness/Assets/Editor/Phase2Build.cs" ]; then
  cp "$repo/unity/Phase2Harness/Assets/Editor/Phase2Build.cs" "$assets/Editor/"
fi

echo "assembled Phase 2 assets -> $project"
