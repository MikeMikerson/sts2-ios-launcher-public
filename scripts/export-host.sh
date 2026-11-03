#!/usr/bin/env bash
# Export the host project with Godot: the Xcode project and the main pack. Run tools/prepare_full_game_project.py first.
# No game file takes part. The framework in this export is an empty stub that tools/mono/build_mono.py replaces.
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
python=tools/python/bin/python3
host=src/STS2Mobile.FullGame
out=analysis/full-game-ios

for link in "$host"/*; do
    if [[ -L "$link" ]]; then
        echo "The host project must not link into another tree: $link. Run tools/prepare_full_game_project.py." >&2
        exit 1
    fi
done

# Always export from scratch into a new directory, so no stale object or pack from an earlier or interrupted build can be
# picked up.
"$python" tools/xcode_local.py capture
rm -rf "$host/.godot/mono/temp" "$out/export"
mkdir -p "$out/export"
"$python" tools/full_game_build_inputs.py snapshot "$out/build-inputs.json"

scripts/godot.sh --headless --editor --path "$host" \
    --export-release 'iOS Full Game' "$repo_root/$out/export/FullGame.xcodeproj" \
    > "$out/export.log" 2>&1
if grep -q 'ERROR:' "$out/export.log"; then
    echo "Host export failed. Inspect $out/export.log and the Godot MSBuild log; stale output is rejected." >&2
    exit 1
fi
"$python" tools/full_game_build_inputs.py verify "$out/build-inputs.json"
"$python" tools/prepare_full_game_xcode.py
